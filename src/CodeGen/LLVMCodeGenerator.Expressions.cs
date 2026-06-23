using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// Expression code generation: allocation, member variable access, method calls, operators.
/// </summary>
public partial class LlvmCodeGenerator
{
    private const string FloatTypeName = "float";
    private const string DoubleTypeName = "double";
    private const string Fp128TypeName = "fp128";

    /// <summary>
    /// Emits code for any expression node.
    /// </summary>
    /// <param name="sb">The builder receiving emitted LLVM IR.</param>
    /// <param name="expr">The expression to emit.</param>
    /// <returns>The temporary value name produced for the expression.</returns>
    private string EmitExpression(StringBuilder sb, Expression expr)
    {
        return expr switch
        {
            // TODO: This should be eliminated by lowering pass
            LiteralExpression literal => EmitLiteral(sb: sb, literal: literal),
            IdentifierExpression identifier => EmitIdentifier(sb: sb, identifier: identifier),
            // TODO: This should be eliminated by lowering pass
            MemberExpression memberAccess => EmitMemberVariableAccess(sb: sb, expr: memberAccess),
            CreatorExpression constructor => EmitConstructorCall(sb: sb, expr: constructor),
            CallExpression call => EmitCall(sb: sb, call: call),
            // TODO: This should be eliminated by lowering pass
            BinaryExpression binary => EmitBinaryOp(sb: sb, binary: binary),
            // TODO: This should be eliminated by lowering pass
            UnaryExpression unary => EmitUnaryOp(sb: sb, unary: unary),
            GenericMethodCallExpression gmc => EmitGmceFallback(sb: sb, gmc: gmc),
            // Array[T,N] and BitArray[N] are inline IR constructs (insertvalue); all other
            // collection literals must be lowered to CreatorExpression + add calls before codegen.
            ListLiteralExpression list when IsArrayOrBitArrayLiteral(list.ResolvedType) =>
                EmitListLiteral(sb: sb, list: list),
            // TODO: This should be eliminated by lowering pass
            CarrierPayloadExpression payload => EmitCarrierPayloadExpression(sb: sb,
                payload: payload),
            // Named arguments appear inside synthesized AST bodies (e.g., me.$eq(you: you)).
            // The name is irrelevant to codegen -> just emit the inner value positionally.
            NamedArgumentExpression named => EmitExpression(sb: sb, expr: named.Value),
            _ => throw new NotImplementedException(
                message: $"Expression type not implemented: {expr.GetType().Name}")
        };
    }

    /// <summary>
    /// Materializes a lifted lambda as a heap closure object <c>{ fn_ptr, capture0, ... }</c> and
    /// returns the pointer to it. Captured values are loaded from the current scope's locals at this
    /// (capture) site. The closure is the Routine value: indirect calls load the function pointer
    /// from field 0 and pass the closure pointer as the hidden leading argument.
    /// </summary>
    private string EmitClosureValue(StringBuilder sb, RoutineInfo lambda)
    {
        string clStruct = ClosureStructName(lambda: lambda);
        string fnSym = $"@{MangleRoutineName(routine: lambda)}";

        string sizeTemp = NextTemp();
        EmitLine(sb: sb, line: $"  {sizeTemp} = getelementptr {clStruct}, ptr null, i32 1");
        string size = NextTemp();
        EmitLine(sb: sb, line: $"  {size} = ptrtoint ptr {sizeTemp} to i64");
        string clPtr = NextTemp();
        EmitLine(sb: sb, line: $"  {clPtr} = call ptr @rf_allocate_dynamic(i64 {size})");

        // Field 0: the function pointer.
        string fnFieldPtr = NextTemp();
        EmitLine(sb: sb,
            line: $"  {fnFieldPtr} = getelementptr {clStruct}, ptr {clPtr}, i32 0, i32 0");
        EmitLine(sb: sb, line: $"  store ptr {fnSym}, ptr {fnFieldPtr}");

        // Fields 1..n: the captured values, loaded from the locals live at this capture site.
        if (lambda.ClosureCaptures != null)
        {
            for (int i = 0; i < lambda.ClosureCaptures.Count; i++)
            {
                (string capName, TypeInfo capType) = lambda.ClosureCaptures[index: i];
                string capLlvm = GetLlvmType(type: capType);
                string llvmName =
                    _localVarLlvmNames.GetValueOrDefault(key: capName, defaultValue: capName);
                string capVal = NextTemp();
                EmitLine(sb: sb, line: $"  {capVal} = load {capLlvm}, ptr %{llvmName}.addr");
                string capFieldPtr = NextTemp();
                EmitLine(sb: sb,
                    line: $"  {capFieldPtr} = getelementptr {clStruct}, ptr {clPtr}, i32 0, i32 {i + 1}");
                EmitLine(sb: sb, line: $"  store {capLlvm} {capVal}, ptr {capFieldPtr}");
            }
        }

        return clPtr;
    }

    /// <summary>
    /// Materializes a plain (non-lambda) routine reference as a captureless heap closure
    /// <c>{ fn_ptr }</c> whose function slot holds a closure-ABI adapter thunk (see
    /// <see cref="EnsureRoutineValueThunk"/>). This lets a bare routine name flow through the
    /// same indirect-call path as a lambda value.
    /// </summary>
    private string EmitRoutineValueClosure(StringBuilder sb, RoutineInfo routine)
    {
        string thunkSym = EnsureRoutineValueThunk(routine: routine);

        // Closure is just { ptr } — no captures. Allocate 8 bytes and store the thunk pointer.
        string sizeTemp = NextTemp();
        EmitLine(sb: sb, line: $"  {sizeTemp} = getelementptr ptr, ptr null, i32 1");
        string size = NextTemp();
        EmitLine(sb: sb, line: $"  {size} = ptrtoint ptr {sizeTemp} to i64");
        string clPtr = NextTemp();
        EmitLine(sb: sb, line: $"  {clPtr} = call ptr @rf_allocate_dynamic(i64 {size})");
        EmitLine(sb: sb, line: $"  store ptr {thunkSym}, ptr {clPtr}");
        return clPtr;
    }

    /// <summary>
    /// Ensures a closure-ABI adapter thunk exists for a plain free routine used as a value, and
    /// returns its symbol. The thunk has the routine's signature with a hidden leading
    /// <c>ptr %__cl</c> (ignored); it forwards the remaining arguments to the real routine. One
    /// thunk per routine (deduped). Restricted to free routines — a bare method name carries no
    /// receiver, so method-as-value is not supported here.
    /// </summary>
    private string EnsureRoutineValueThunk(RoutineInfo routine)
    {
        // MangleRoutineName already quotes the symbol when it contains special chars (parens), so
        // strip any surrounding quotes to recover the raw name, append the thunk suffix, re-quote.
        string mangled = MangleRoutineName(routine: routine);
        string realRef = $"@{mangled}";
        string rawName = mangled.StartsWith(value: '"') ? mangled[1..^1] : mangled;
        string thunkRaw = $"{rawName}$rfvthunk";
        string thunkSym = $"@{Q(name: thunkRaw)}";
        if (!_emittedRoutineValueThunks.Add(item: thunkRaw))
            return thunkSym;

        string returnType = routine.ReturnType != null
            ? GetLlvmType(type: routine.ReturnType)
            : "void";
        returnType = routine.AsyncStatus switch
        {
            AsyncStatus.LookupVariant => GetLookupCarrierLlvmType(valueType: routine.ReturnType!),
            AsyncStatus.CheckVariant => GetResultCarrierLlvmType(valueType: routine.ReturnType!),
            AsyncStatus.TryBoolVariant => "i1",
            _ => returnType
        };

        var paramDecls = new List<string> { "ptr %__cl" };
        var fwdTypes = new List<string>();
        var fwdValues = new List<string>();
        for (int i = 0; i < routine.Parameters.Count; i++)
        {
            string pType = GetParameterLlvmType(type: routine.Parameters[index: i].Type);
            string pName = $"%a{i}";
            paramDecls.Add(item: $"{pType} {pName}");
            fwdTypes.Add(item: pType);
            fwdValues.Add(item: pName);
        }

        var b = _auxRoutineDefinitions;
        b.Append(value:
            $"define {returnType} {thunkSym}({string.Join(separator: ", ", values: paramDecls)}) {{\n");
        b.Append(value: "entry:\n");
        string fwdArgs = string.Join(separator: ", ",
            values: fwdTypes.Select(selector: (t, i) => $"{t} {fwdValues[index: i]}"));
        if (returnType == "void")
        {
            b.Append(value: $"  call void {realRef}({fwdArgs})\n");
            b.Append(value: "  ret void\n}\n");
        }
        else
        {
            b.Append(value: $"  %r = call {returnType} {realRef}({fwdArgs})\n");
            b.Append(value: $"  ret {returnType} %r\n}}\n");
        }
        return thunkSym;
    }

    // ── v0.1 concurrency: threaded routines + Task[T].waitfor ──────────────────────

    /// <summary>
    /// Registers <c>declare</c> lines for the task runtime functions used by hand-emitted
    /// threaded-spawn / waitfor IR. Idempotent (keyed by function name).
    /// </summary>
    private void EnsureTaskRuntimeDeclares()
    {
        // Only the spawn/thunk-exclusive runtime functions are declared here, with the raw `ptr`
        // ABI the hand-emitted IR uses. The wait-side functions (rf_task_wait/wait_within/
        // result_payload/destroy) are called from the RF stdlib `Task.retrieve!`, so the normal
        // extern-declaration path emits them with the RF `Address`(=i64) ABI — declaring them here
        // too would clash. rf_invalidate is shared, so it matches the existing i64 convention.
        _rfRoutineDeclarations[key: "rf_task_create"] = "declare ptr @rf_task_create(i32)";
        _rfRoutineDeclarations[key: "rf_task_spawn_threaded"] =
            "declare i32 @rf_task_spawn_threaded(ptr, ptr, ptr)";
        _rfRoutineDeclarations[key: "rf_task_complete_value"] =
            "declare void @rf_task_complete_value(ptr, ptr)";
        _rfRoutineDeclarations[key: "rf_allocate_dynamic"] =
            "declare ptr @rf_allocate_dynamic(i64)";
        _rfRoutineDeclarations[key: "rf_invalidate"] = "declare void @rf_invalidate(i64)";
    }

    /// <summary>
    /// Ensures the <c>%"Argpack.&lt;routine&gt;"</c> struct type (one field per parameter, in
    /// declaration order) is declared, and returns its name. Used to ferry a threaded call's
    /// arguments across the thread boundary as the task userdata.
    /// </summary>
    private string EnsureArgpackType(RoutineInfo routine)
    {
        string mangled = MangleRoutineName(routine: routine);
        string raw = mangled.StartsWith(value: '"') ? mangled[1..^1] : mangled;
        string name = $"%{Q(name: $"Argpack.{raw}")}";
        if (!_typeDeclarationsClosure.ContainsKey(key: name))
        {
            var fields = routine.Parameters
                                .Select(selector: p => IsByRefThreadArg(routine: routine, param: p)
                                    ? "ptr"
                                    : GetParameterLlvmType(type: p.Type))
                                .ToList();
            _typeDeclarationsClosure[key: name] =
                $"{name} = type {{ {string.Join(separator: ", ", values: fields)} }}\n";
        }

        return name;
    }

    /// <summary>
    /// Emits the entry thunk <c>@&lt;routine&gt;$thread_entry(ptr %task, ptr %userdata)</c>
    /// matching the C <c>rf_task_entry_fn</c> signature. It unpacks the argpack, calls the real
    /// routine, heap-boxes the result, completes the task, and frees the argpack. One per routine.
    /// </summary>
    private string EnsureThreadEntryThunk(RoutineInfo routine)
    {
        string mangled = MangleRoutineName(routine: routine);
        string realRef = $"@{mangled}";
        string raw = mangled.StartsWith(value: '"') ? mangled[1..^1] : mangled;
        string thunkRaw = $"{raw}$thread_entry";
        string thunkSym = $"@{Q(name: thunkRaw)}";
        if (!_emittedRoutineValueThunks.Add(item: thunkRaw))
            return thunkSym;

        string retType = routine.ReturnType != null
            ? GetLlvmType(type: routine.ReturnType)
            : "void";

        StringBuilder b = _auxRoutineDefinitions;
        b.Append(value: $"define void {thunkSym}(ptr %task, ptr %userdata) {{\n");
        b.Append(value: "entry:\n");

        var callArgs = new List<string>();
        if (routine.Parameters.Count > 0)
        {
            string packType = EnsureArgpackType(routine: routine);
            for (int i = 0; i < routine.Parameters.Count; i++)
            {
                // By-ref thread args are stored as a `ptr` (the spawner's cell address); load and
                // forward the pointer so the worker's by-ref parameter aliases the shared cell.
                string pt = IsByRefThreadArg(routine: routine, param: routine.Parameters[index: i])
                    ? "ptr"
                    : GetParameterLlvmType(type: routine.Parameters[index: i].Type);
                b.Append(value:
                    $"  %fp{i} = getelementptr {packType}, ptr %userdata, i32 0, i32 {i}\n");
                b.Append(value: $"  %a{i} = load {pt}, ptr %fp{i}\n");
                callArgs.Add(item: $"{pt} %a{i}");
            }
        }

        string argList = string.Join(separator: ", ", values: callArgs);
        if (retType == "void")
        {
            b.Append(value: $"  call void {realRef}({argList})\n");
            b.Append(value: "  call void @rf_task_complete_value(ptr %task, ptr null)\n");
        }
        else
        {
            b.Append(value: $"  %r = call {retType} {realRef}({argList})\n");
            b.Append(value: $"  %bsz.p = getelementptr {retType}, ptr null, i32 1\n");
            b.Append(value: "  %bsz = ptrtoint ptr %bsz.p to i64\n");
            b.Append(value: "  %box = call ptr @rf_allocate_dynamic(i64 %bsz)\n");
            b.Append(value: $"  store {retType} %r, ptr %box\n");
            b.Append(value: "  call void @rf_task_complete_value(ptr %task, ptr %box)\n");
        }

        if (routine.Parameters.Count > 0)
        {
            // rf_invalidate takes Address (i64); ptrtoint the userdata pointer first.
            b.Append(value: "  %udint = ptrtoint ptr %userdata to i64\n");
            b.Append(value: "  call void @rf_invalidate(i64 %udint)\n");
        }
        b.Append(value: "  ret void\n}\n");
        return thunkSym;
    }

    /// <summary>
    /// Lowers a <c>threaded routine foo(args)</c> call: boxes the arguments, creates an OS-thread
    /// task, and spawns it on the generated entry thunk. The expression value is the
    /// <c>rf_task*</c> handle (<c>Task[T]</c> lowers to <c>ptr</c>).
    /// </summary>
    private string EmitThreadedSpawn(StringBuilder sb, RoutineInfo routine,
        List<Expression> arguments)
    {
        EnsureTaskRuntimeDeclares();
        GenerateRoutineDeclaration(routine: routine);
        string entryThunk = EnsureThreadEntryThunk(routine: routine);

        int n = Math.Min(val1: arguments.Count, val2: routine.Parameters.Count);
        var values = new List<string>();
        var types = new List<string>();
        for (int i = 0; i < n; i++)
        {
            // By-ref struct-record arg: marshal the spawner's cell ADDRESS (not a copy) so the
            // worker's by-ref parameter shares the one cell.
            if (IsByRefThreadArg(routine: routine, param: routine.Parameters[index: i]))
            {
                string addr = EmitLvalueAddress(sb: sb, expr: arguments[index: i]);
                values.Add(item: addr);
                types.Add(item: "ptr");
                continue;
            }

            string v = EmitExpression(sb: sb, expr: arguments[index: i]);
            TypeInfo actual = GetExpressionType(expr: arguments[index: i])
                              ?? routine.Parameters[index: i].Type!;
            (string cv, string _) = CoerceCallArgumentToParameter(sb: sb,
                argValue: v,
                actualType: actual,
                parameterType: routine.Parameters[index: i].Type);
            values.Add(item: cv);
            types.Add(item: GetParameterLlvmType(type: routine.Parameters[index: i].Type));
        }

        string userdata = "ptr null";
        if (values.Count > 0)
        {
            string packType = EnsureArgpackType(routine: routine);
            string szp = NextTemp();
            EmitLine(sb: sb, line: $"  {szp} = getelementptr {packType}, ptr null, i32 1");
            string sz = NextTemp();
            EmitLine(sb: sb, line: $"  {sz} = ptrtoint ptr {szp} to i64");
            string pack = NextTemp();
            EmitLine(sb: sb, line: $"  {pack} = call ptr @rf_allocate_dynamic(i64 {sz})");
            for (int i = 0; i < values.Count; i++)
            {
                string fp = NextTemp();
                EmitLine(sb: sb,
                    line: $"  {fp} = getelementptr {packType}, ptr {pack}, i32 0, i32 {i}");
                EmitLine(sb: sb, line: $"  store {types[index: i]} {values[index: i]}, ptr {fp}");
            }

            userdata = $"ptr {pack}";
        }

        string task = NextTemp();
        EmitLine(sb: sb, line: $"  {task} = call ptr @rf_task_create(i32 1)");
        string spawnRc = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {spawnRc} = call i32 @rf_task_spawn_threaded(ptr {task}, ptr {entryThunk}, {userdata})");

        // spawned = (task != null) && (spawn rc != 0). rf_task_spawn_threaded returns 1 on
        // success, 0 on failure.
        string createOk = NextTemp();
        EmitLine(sb: sb, line: $"  {createOk} = icmp ne ptr {task}, null");
        string spawnOk = NextTemp();
        EmitLine(sb: sb, line: $"  {spawnOk} = icmp ne i32 {spawnRc}, 0");
        string spawned = NextTemp();
        EmitLine(sb: sb, line: $"  {spawned} = and i1 {createOk}, {spawnOk}");

        // Build the Task[T] record value: { task_handle, deadline (zero), has_deadline (false),
        // spawned }. deadline/has_deadline stay at their zeroinitializer defaults.
        TypeInfo taskType = _registry.GetOrCreateResolution(
            genericDef: _registry.LookupType(name: "Task")!,
            typeArguments: [routine.ReturnType!]);
        string recLlvm = GetLlvmType(type: taskType);
        // Address lowers to i64, so the handle pointer must be ptrtoint'd into field 0.
        string taskInt = NextTemp();
        EmitLine(sb: sb, line: $"  {taskInt} = ptrtoint ptr {task} to i64");
        string r0 = NextTemp();
        EmitLine(sb: sb, line: $"  {r0} = insertvalue {recLlvm} zeroinitializer, i64 {taskInt}, 0");
        string r1 = NextTemp();
        EmitLine(sb: sb, line: $"  {r1} = insertvalue {recLlvm} {r0}, i1 {spawned}, 3");
        return r1;
    }

    /// <summary>
    /// Generates a variable reference.
    /// </summary>
    private string EmitIdentifier(StringBuilder sb, IdentifierExpression identifier)
    {
        // Const generic value: identifier retains its original param name (e.g., "N")
        // Resolve via _typeSubstitutions to get the numeric value
        if (_typeSubstitutions != null &&
            _typeSubstitutions.TryGetValue(key: identifier.Name, value: out TypeInfo? subType) &&
            subType is ConstGenericValueTypeInfo constVal)
        {
            return constVal.Value.ToString();
        }

        // Aggregate (Array[T,N]) presets are not inlined — they live in a shared `@preset.*`
        // constant global. A value-position read loads the whole array from the global.
        if (ResolveAggregatePreset(name: identifier.Name) is { } aggregatePreset)
        {
            string presetSym = EmitOrGetPresetGlobal(preset: aggregatePreset);
            string arrLlvm = GetLlvmType(type: aggregatePreset.Type);
            string loaded = NextTemp();
            EmitLine(sb: sb, line: $"  {loaded} = load {arrLlvm}, ptr {presetSym}");
            return loaded;
        }

        // Scalar presets must be inlined before backend entry. Codegen should not read declaration
        // AST to recover their values on demand.
        if (_registry.LookupVariable(name: identifier.Name) is { IsPreset: true })
        {
            throw new InvalidOperationException(
                message:
                $"Preset identifier '{identifier.Name}' reached LLVM codegen. PresetInliningPass must inline presets before backend entry.");
        }

        if (identifier.ResolvedType is RoutineTypeInfo routineType && TryResolveRoutineReference(
                name: identifier.Name,
                routineType: routineType,
                routine: out RoutineInfo? routine))
        {
            // A lifted lambda referenced as a VALUE (passed as an argument, returned, stored) is
            // materialized as a heap closure object so it can carry its captures and be called
            // through the uniform closure ABI. A plain (non-lambda) routine reference stays a bare
            // function-pointer symbol.
            if (routine!.IsLambda)
            {
                return EmitClosureValue(sb: sb, lambda: routine);
            }
            // A plain (non-lambda) routine used as a Routine VALUE must also present the uniform
            // closure ABI so the indirect call site (which loads fn from closure[0] and passes the
            // closure as the hidden leading arg) works. Wrap it in a captureless closure whose fn
            // slot is an adapter thunk that drops the closure arg and forwards to the real routine.
            return EmitRoutineValueClosure(sb: sb, routine: routine);
        }

        // Look up the variable in local variables first
        if (!_localVariables.TryGetValue(key: identifier.Name, value: out TypeInfo? varType))
        {
            throw new InvalidOperationException(
                message: $"Unknown identifier '{identifier.Name}'");
        }

        // Variables are stored in allocas (%name.addr), need to load them
        // Use unique LLVM name to handle shadowing
        string llvmName =
            _localVarLlvmNames.TryGetValue(key: identifier.Name, value: out string? unique)
                ? unique
                : identifier.Name;
        string llvmType = GetLlvmType(type: varType);
        string tmp = NextTemp();
        EmitLine(sb: sb, line: $"  {tmp} = load {llvmType}, ptr %{llvmName}.addr");
        return tmp;
    }

    /// <summary>
    /// Attempts to resolve routine reference and reports whether it succeeded.
    /// </summary>
    private bool TryResolveRoutineReference(string name, RoutineTypeInfo routineType,
        out RoutineInfo? routine)
    {
        routine = null;
        string bareName = name.EndsWith(value: '!')
            ? name[..^1]
            : name;
        string? moduleName = _currentEmittingRoutine?.OwnerType?.Module ??
                             _currentEmittingRoutine?.Module;

        List<TypeInfo> paramTypes = routineType.ParameterTypes.ToList();

        if (moduleName != null && !bareName.Contains(value: '.'))
        {
            routine = _registry.LookupRoutineOverload(baseName: $"{moduleName}.{bareName}",
                argTypes: paramTypes);
            routine ??= _registry.LookupRoutine(fullName: $"{moduleName}.{bareName}");
        }

        routine ??= _registry.LookupRoutineOverload(baseName: bareName, argTypes: paramTypes);
        routine ??= _registry.LookupRoutine(fullName: bareName);
        routine ??=
            _registry.LookupRoutineByName(name: bareName, isFailable: routineType.IsFailable);
        return routine != null;
    }

    /// <summary>
    /// Emits a call to <c>@_rf_trace_update_loc</c> with the call site's source line/col baked
    /// in as constants. This refreshes the topmost shadow-stack frame so a subsequent throw
    /// produces a stack trace pointing at the actual call site within the enclosing routine
    /// rather than the routine's declaration line. Skips when trace emission is disabled or
    /// when the location is unset (synthesized AST nodes).
    /// </summary>
    private void EmitTraceLocUpdate(StringBuilder sb, SourceLocation? location)
    {
        if (!_traceCurrentRoutine) return;
        if (location == null) return;
        int line = location.Line;
        int col = location.Column;
        if (line <= 0 && col <= 0) return;
        EmitLine(sb: sb,
            line: $"  call void @_rf_trace_update_loc(i32 {line}, i32 {col})");
    }

    /// <summary>
    /// Generates code for a function/method call.
    /// Handles both standalone function calls and method calls on objects.
    /// </summary>
    private string EmitCall(StringBuilder sb, CallExpression call)
    {
        EmitTraceLocUpdate(sb: sb, location: call.Location);

        return call.Callee switch
        {
            // Determine if this is a method call (callee is MemberExpression) or standalone function call
            MemberExpression member => EmitMemberRoutineCall(sb: sb,
                member: member,
                arguments: call.Arguments,
                resolvedRoutine: call.ResolvedRoutine,
                typeArguments: call.TypeArguments,
                loweringKind: call.LoweringKind),
            IdentifierExpression id => EmitRoutineCall(sb: sb,
                req: new RoutineCallRequest(FunctionName: id.Name,
                    Arguments: call.Arguments,
                    ResolvedRoutine: call.ResolvedRoutine,
                    ResolvedReturnType: call.ResolvedType,
                    TypeArguments: call.TypeArguments,
                    LoweringKind: call.LoweringKind,
                    ConstructedType: call.ConstructedType)),
            _ => throw new NotImplementedException(
                message: $"Cannot emit call for callee type: {call.Callee.GetType().Name}")
        };
    }

    /// <summary>
    /// Emit backend scalar cast as part of this compiler phase.
    /// </summary>
    private string EmitBackendScalarCast(StringBuilder sb, string value, TypeInfo? sourceType,
        TypeInfo targetType)
    {
        string targetLlvm = GetLlvmType(type: targetType);
        string sourceLlvm = sourceType != null
            ? GetLlvmType(type: sourceType)
            : targetLlvm;

        if (sourceLlvm == targetLlvm)
        {
            return value;
        }

        if (targetLlvm == "ptr" && sourceLlvm != "ptr")
        {
            string cast = NextTemp();
            EmitLine(sb: sb, line: $"  {cast} = inttoptr {sourceLlvm} {value} to ptr");
            return cast;
        }

        if (targetLlvm != "ptr" && sourceLlvm == "ptr")
        {
            string cast = NextTemp();
            EmitLine(sb: sb, line: $"  {cast} = ptrtoint ptr {value} to {targetLlvm}");
            return cast;
        }

        if (TryGetLlvmIntegerWidth(sourceLlvm, out int sourceIntBits) &&
            TryGetLlvmIntegerWidth(targetLlvm, out int targetIntBits))
        {
            string integerResult = NextTemp();
            if (sourceIntBits > targetIntBits)
            {
                EmitLine(sb: sb,
                    line: $"  {integerResult} = trunc {sourceLlvm} {value} to {targetLlvm}");
            }
            else if (sourceIntBits < targetIntBits)
            {
                string op = IsUnsignedIntegerType(type: targetType)
                    ? "zext"
                    : "sext";
                EmitLine(sb: sb,
                    line: $"  {integerResult} = {op} {sourceLlvm} {value} to {targetLlvm}");
            }
            else
            {
                EmitLine(sb: sb,
                    line: $"  {integerResult} = bitcast {sourceLlvm} {value} to {targetLlvm}");
            }

            return integerResult;
        }

        bool sourceIsFloat = sourceLlvm is "half" or FloatTypeName or DoubleTypeName or Fp128TypeName;
        bool targetIsFloat = targetLlvm is "half" or FloatTypeName or DoubleTypeName or Fp128TypeName;
        bool targetUnsigned = IsUnsignedIntegerType(type: targetType);

        string result = NextTemp();
        if (sourceIsFloat && targetIsFloat)
        {
            string op = GetTypeBitWidth(llvmType: sourceLlvm) >
                        GetTypeBitWidth(llvmType: targetLlvm)
                ? "fptrunc"
                : "fpext";
            EmitLine(sb: sb, line: $"  {result} = {op} {sourceLlvm} {value} to {targetLlvm}");
        }
        else if (sourceIsFloat)
        {
            string op = targetUnsigned
                ? "fptoui"
                : "fptosi";
            EmitLine(sb: sb, line: $"  {result} = {op} {sourceLlvm} {value} to {targetLlvm}");
        }
        else if (targetIsFloat)
        {
            bool sourceUnsigned = IsUnsignedIntegerType(type: sourceType);
            string op = sourceUnsigned
                ? "uitofp"
                : "sitofp";
            EmitLine(sb: sb, line: $"  {result} = {op} {sourceLlvm} {value} to {targetLlvm}");
        }
        else
        {
            int srcBits = GetTypeBitWidth(llvmType: sourceLlvm);
            int dstBits = GetTypeBitWidth(llvmType: targetLlvm);
            if (srcBits > dstBits)
            {
                EmitLine(sb: sb, line: $"  {result} = trunc {sourceLlvm} {value} to {targetLlvm}");
            }
            else if (srcBits < dstBits)
            {
                string op = targetUnsigned
                    ? "zext"
                    : "sext";
                EmitLine(sb: sb, line: $"  {result} = {op} {sourceLlvm} {value} to {targetLlvm}");
            }
            else
            {
                EmitLine(sb: sb,
                    line: $"  {result} = bitcast {sourceLlvm} {value} to {targetLlvm}");
            }
        }

        return result;
    }

    /// <summary>
    /// Attempts to get LLVM integer width and reports whether it succeeded.
    /// </summary>
    private static bool TryGetLlvmIntegerWidth(string llvmType, out int bitWidth)
    {
        bitWidth = 0;
        if (!llvmType.StartsWith('i') || llvmType.Length < 2)
        {
            return false;
        }

        return int.TryParse(llvmType.AsSpan(start: 1), out bitWidth);
    }

    /// <summary>
    /// Emits a primitive type cast (trunc/zext/sext/bitcast) from one LLVM primitive type to another.
    /// Used when an explicitly typed variable declaration has an initializer of a different type.
    /// </summary>
    private string EmitPrimitiveCast(StringBuilder sb, string value, string fromLlvm,
        string toLlvm)
    {
        if (fromLlvm == toLlvm) return value;

        bool fromIsFloat = fromLlvm is "half" or FloatTypeName or DoubleTypeName or Fp128TypeName;
        bool toIsFloat = toLlvm is "half" or FloatTypeName or DoubleTypeName or Fp128TypeName;

        string cast = NextTemp();
        if (fromIsFloat && toIsFloat)
        {
            string op = GetTypeBitWidth(llvmType: fromLlvm) > GetTypeBitWidth(llvmType: toLlvm)
                ? "fptrunc"
                : "fpext";
            EmitLine(sb: sb, line: $"  {cast} = {op} {fromLlvm} {value} to {toLlvm}");
        }
        else if (fromIsFloat)
        {
            EmitLine(sb: sb, line: $"  {cast} = fptosi {fromLlvm} {value} to {toLlvm}");
        }
        else if (toIsFloat)
        {
            EmitLine(sb: sb, line: $"  {cast} = sitofp {fromLlvm} {value} to {toLlvm}");
        }
        else
        {
            int srcBits = GetTypeBitWidth(llvmType: fromLlvm);
            int dstBits = GetTypeBitWidth(llvmType: toLlvm);
            string op = "bitcast";
            if (srcBits > dstBits)
            {
                op = "trunc";
            }
            else if (srcBits < dstBits)
            {
                op = "zext";
            }

            EmitLine(sb: sb, line: $"  {cast} = {op} {fromLlvm} {value} to {toLlvm}");
        }

        return cast;
    }

    /// <summary>
    /// Emit binary op as part of this compiler phase.
    /// </summary>
    private string EmitBinaryOp(StringBuilder sb, BinaryExpression binary)
    {
        // TODO: This should be done with member routine, not here
        return binary.Operator switch
        {
            BinaryOperator.And => throw new InvalidOperationException(
                $"BinaryExpression(And) must be lowered to ConditionalExpression by ExpressionLoweringPass before codegen. In routine: {_currentEmittingRoutine?.Name ?? "<unknown>"} (owner: {_currentEmittingRoutine?.OwnerType?.Name ?? "none"})"),
            BinaryOperator.Or => throw new InvalidOperationException(
                $"BinaryExpression(Or) must be lowered to ConditionalExpression by ExpressionLoweringPass before codegen. In routine: {_currentEmittingRoutine?.Name ?? "<unknown>"} (owner: {_currentEmittingRoutine?.OwnerType?.Name ?? "none"})"),
            BinaryOperator.Assign => EmitBinaryAssign(sb: sb, binary: binary),
            BinaryOperator.In => EmitContainsCall(sb: sb, binary: binary, methodName: "$contains"),
            BinaryOperator.NotIn => EmitContainsCall(sb: sb,
                binary: binary,
                methodName: "$notcontains"),
            BinaryOperator.Is => EmitChoiceIs(sb: sb, binary: binary, cmpOp: "eq"),
            BinaryOperator.IsNot => EmitChoiceIs(sb: sb, binary: binary, cmpOp: "ne"),
            BinaryOperator.Obeys => EmitCompileTimeConstant(value: "true"),
            BinaryOperator.Disobeys => EmitCompileTimeConstant(value: "false"),
            // Flags types intentionally bypass method-call lowering for bitwise ops to avoid
            // infinite recursion in synthesized $bitor/$bitand bodies. They reach codegen
            // unlowered and are emitted as direct LLVM bitwise instructions on the underlying
            // integer representation. See OperatorLoweringPass.cs (around the FlagsTypeInfo
            // skip) for the symmetric end.
            BinaryOperator.BitwiseOr when GetExpressionType(expr: binary.Left) is FlagsTypeInfo
                => EmitFlagsBitwiseOp(sb: sb, binary: binary, llvmOp: "or"),
            BinaryOperator.BitwiseAnd when GetExpressionType(expr: binary.Left) is FlagsTypeInfo
                => EmitFlagsBitwiseOp(sb: sb, binary: binary, llvmOp: "and"),
            BinaryOperator.BitwiseXor when GetExpressionType(expr: binary.Left) is FlagsTypeInfo
                => EmitFlagsBitwiseOp(sb: sb, binary: binary, llvmOp: "xor"),
            // Same skip-list as the bitwise ops above — Flags $eq/$ne bodies use `me == you` /
            // `me != you` (BinaryExpression(Equal/NotEqual)) and OperatorLoweringPass leaves
            // them unlowered for Flags to avoid infinite recursion. Codegen emits a direct
            // `icmp eq/ne` on the underlying integer.
            BinaryOperator.Equal when GetExpressionType(expr: binary.Left) is FlagsTypeInfo
                => EmitFlagsCmpOp(sb: sb, binary: binary, cmpKind: "eq"),
            BinaryOperator.NotEqual when GetExpressionType(expr: binary.Left) is FlagsTypeInfo
                => EmitFlagsCmpOp(sb: sb, binary: binary, cmpKind: "ne"),
            _ => throw new InvalidOperationException(
                $"BinaryExpression({binary.Operator}) must be lowered to a wired call before codegen " +
                $"(left={binary.Left.GetType().Name}, loc={binary.Location})")
        };
    }

    private string EmitFlagsBitwiseOp(StringBuilder sb, BinaryExpression binary, string llvmOp)
    {
        string left = EmitExpression(sb: sb, expr: binary.Left);
        string right = EmitExpression(sb: sb, expr: binary.Right);
        TypeInfo? flagsType = GetExpressionType(expr: binary.Left);
        string llvmType = flagsType != null ? GetLlvmType(type: flagsType) : "i64";
        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = {llvmOp} {llvmType} {left}, {right}");
        return result;
    }

    private string EmitFlagsCmpOp(StringBuilder sb, BinaryExpression binary, string cmpKind)
    {
        string left = EmitExpression(sb: sb, expr: binary.Left);
        string right = EmitExpression(sb: sb, expr: binary.Right);
        TypeInfo? flagsType = GetExpressionType(expr: binary.Left);
        string llvmType = flagsType != null ? GetLlvmType(type: flagsType) : "i64";
        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = icmp {cmpKind} {llvmType} {left}, {right}");
        return result;
    }

    /// <summary>
    /// Emit binary assign as part of this compiler phase.
    /// </summary>
    private string EmitBinaryAssign(StringBuilder sb, BinaryExpression binary)
    {
        // TODO: This should be done with member routine, not here
        if (binary.Left is IndexExpression idxLhs)
        {
            EmitIndexAssignment(sb: sb, index: idxLhs, rhs: binary.Right);
            return "undef";
        }

        string value = EmitExpression(sb: sb, expr: binary.Right);

        switch (binary.Left)
        {
            case IdentifierExpression id:
                EmitVariableAssignment(sb: sb, varName: id.Name, value: value);
                break;
            case MemberExpression member:
            {
                EmitMemberVariableAssignment(sb: sb,
                    member: member,
                    value: value,
                    valueType: GetExpressionType(expr: binary.Right));
                if (binary.Right is IdentifierExpression { Name: var srcRcName })
                {
                    _localRetainedVars.RemoveAll(match: e => e.Name == srcRcName);
                }
                // Ownership transfer: `me.field = local` (post-steal-strip) or `me.field = local`
                // hands the local's heap allocation to the field. Without removing the local
                // from _localEntityVars, the function-exit cleanup would re-free the pointer
                // that now lives in the field → double-free at scope exit.
                ConsumeTransferredLocalOwnership(expr: binary.Right);

                break;
            }
            default:
                throw new NotImplementedException(
                    message:
                    $"Assignment target not implemented for expression type: {binary.Left.GetType().Name}");
        }

        return value;
    }

    /// <summary>
    /// Emit contains call as part of this compiler phase.
    /// </summary>
    private string EmitContainsCall(StringBuilder sb, BinaryExpression binary, string methodName)
    {
        // TODO: This should be done with member routine, not here
        string collection = EmitExpression(sb: sb, expr: binary.Right);
        string element = EmitExpression(sb: sb, expr: binary.Left);

        TypeInfo? collectionType = GetExpressionType(expr: binary.Right);
        if (collectionType == null)
        {
            throw new InvalidOperationException(
                message: "Cannot determine collection type for 'in'/'notin' operator");
        }

        ResolvedMemberRoutine? resolved =
            ResolveMemberRoutine(receiverType: collectionType, methodName: methodName);
        string mangledName = resolved?.MangledName ??
                             Q(name:
                                 $"{collectionType.FullName}.{SanitizeLlvmName(name: methodName)}");

        if (resolved != null)
        {
            GenerateRoutineDeclaration(routine: resolved.Routine,
                nameOverride: resolved.MangledName);
        }

        var argValues = new List<string> { collection, element };
        var argTypes = new List<string> { GetParameterLlvmType(type: collectionType) };

        argTypes.Add(item: GetExpressionLlvmType(expr: binary.Left));

        string result = NextTemp();
        string args = BuildCallArgs(types: argTypes, values: argValues);
        EmitLine(sb: sb, line: $"  {result} = call i1 @{mangledName}({args})");
        return result;
    }

    /// <summary>
    /// Emit choice is as part of this compiler phase.
    /// </summary>
    private string EmitChoiceIs(StringBuilder sb, BinaryExpression binary, string cmpOp)
    {
        string left = EmitExpression(sb: sb, expr: binary.Left);
        string right = EmitExpression(sb: sb, expr: binary.Right);
        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = icmp {cmpOp} i32 {left}, {right}");
        return result;
    }

    /// <summary>
    /// Emit compile time constant as part of this compiler phase.
    /// </summary>
    private static string EmitCompileTimeConstant(string value)
    {
        return value;
    }

    /// <summary>
    /// Emit unary op as part of this compiler phase.
    /// </summary>
    private string EmitUnaryOp(StringBuilder sb, UnaryExpression unary)
    {
        // TODO: This should be done with member routine, not here
        // BitwiseNot on FlagsTypeInfo is intentionally left unlowered by OperatorLoweringPass
        // (flags have no $bitnot body to avoid synthesizer recursion). Emit `xor x, -1` directly
        // on the underlying integer type, mirroring EmitFlagsBitwiseOp.
        if (unary.Operator == UnaryOperator.BitwiseNot &&
            GetExpressionType(expr: unary.Operand) is FlagsTypeInfo flagsType)
        {
            string operand = EmitExpression(sb: sb, expr: unary.Operand);
            string llvmType = GetLlvmType(type: flagsType);
            string result = NextTemp();
            EmitLine(sb: sb, line: $"  {result} = xor {llvmType} {operand}, -1");
            return result;
        }
        return unary.Operator switch
        {
            UnaryOperator.Not => throw new InvalidOperationException(
                $"UnaryExpression(Not) must be lowered to ConditionalExpression by ExpressionLoweringPass before codegen. Routine: {_currentEmittingRoutine?.Name ?? "<unknown>"} (owner: {_currentEmittingRoutine?.OwnerType?.Name ?? "none"})"),
            UnaryOperator.Steal => EmitExpression(sb: sb, expr: unary.Operand),
            _ => throw new InvalidOperationException(
                $"UnaryExpression({unary.Operator}) must be lowered to a wired call before codegen")
        };
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Resolves generic type parameters in a member's type using the owner's type arguments.
    /// Builds a substitution map from the owner and delegates to SubstituteTypeParams.
    /// </summary>
    private TypeInfo ResolveGenericMemberType(TypeInfo memberType, TypeInfo ownerType)
    {
        TypeInfo? ownerGenericDef = ownerType switch
        {
            RecordTypeInfo r => r.GenericDefinition,
            EntityTypeInfo e => e.GenericDefinition,
            _ => null
        };
        if (ownerGenericDef?.GenericParameters == null || ownerType.TypeArguments == null)
        {
            return memberType;
        }

        var subs = new Dictionary<string, TypeInfo>();
        for (int i = 0;
             i < ownerGenericDef.GenericParameters.Count && i < ownerType.TypeArguments.Count;
             i++)
        {
            subs[key: ownerGenericDef.GenericParameters[index: i]] =
                ownerType.TypeArguments[index: i];
        }

        if (subs.Count == 0)
        {
            return memberType;
        }

        return SubstituteTypeParams(type: memberType, substitutions: subs);
    }

    /// <summary>
    /// Emits code for a <see cref="CarrierPayloadExpression"/>: loads the inline payload
    /// (field 1 — a [P x i8] buffer where P = max(sizeof(T), 8)) from a Result/Lookup carrier
    /// as the concrete type.
    ///
    /// <list type="bullet">
    /// <item>Entity / crashable types: payload slot holds a ptr — <c>load ptr</c>.</item>
    /// <item>Record / primitive types: payload slot holds the value inline — <c>load &lt;type&gt;</c>.</item>
    /// </list>
    /// </summary>
    private string EmitCarrierPayloadExpression(StringBuilder sb, CarrierPayloadExpression payload)
    {
        // EmitExpression returns a loaded struct value (not a pointer); GEP needs a pointer.
        // Spill the carrier value to a temp alloca first.
        string carrierVal = EmitExpression(sb: sb, expr: payload.Carrier);

        TypeInfo carrierType = payload.Carrier.ResolvedType!;
        string carrierLlvmType = GetCarrierLlvmType(type: carrierType);

        string spillAddr = NextTemp();
        EmitLine(sb: sb, line: $"  {spillAddr} = alloca {carrierLlvmType}");
        EmitLine(sb: sb, line: $"  store {carrierLlvmType} {carrierVal}, ptr {spillAddr}");

        TypeInfo? concreteType = payload.ResolvedType ?? payload.ConcreteType.ResolvedType ??
            _registry.LookupType(name: payload.ConcreteType.Name);

        string payloadPtr = NextTemp();
        EmitLine(sb: sb,
            line: $"  {payloadPtr} = getelementptr {carrierLlvmType}, ptr {spillAddr}, i32 0, i32 1");

        string loadType = concreteType is EntityTypeInfo or CrashableTypeInfo
            ? "ptr"
            : (concreteType != null ? GetLlvmType(type: concreteType) : "i64");

        string loaded = NextTemp();
        EmitLine(sb: sb, line: $"  {loaded} = load {loadType}, ptr {payloadPtr}");
        return loaded;
    }
}
