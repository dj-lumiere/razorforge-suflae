namespace Compiler.Instantiation.Passes;

using TypeModel.Types;

/// <summary>
/// Phase 6 collection pass: snapshot the concrete generic instances already discovered by
/// semantic analysis and post-verification lowering so closure can materialize bodies for them.
/// </summary>
internal sealed class ReachableGenericCollectionPass(InstantiationContext ctx)
{
    public void Run()
    {
        foreach (TypeInfo concreteType in ctx.Registry.AllConcreteGenericInstances)
        {
            ctx.ReachableGenericTypes.Add(item: concreteType.FullName);

            TypeInfo genericDefinition = concreteType switch
            {
                RecordTypeInfo { GenericDefinition: { } definition } => definition,
                EntityTypeInfo { GenericDefinition: { } definition } => definition,
                _ => concreteType
            };

            foreach (var method in ctx.Registry.GetMethodsForType(genericDefinition))
            {
                ctx.ReachableGenericRoutines.Add(item: $"{concreteType.FullName}.{method.Name}");
            }
        }
    }
}
