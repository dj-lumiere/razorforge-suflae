using TypeModel.Symbols;
using TypeModel.Types;

namespace RazorForge.Tests.TypeModel;

/// <summary>
/// Type-model tests for associated-type storage (bucket 2): verifies that generic
/// monomorphization (<c>CreateInstance</c>) carries protocol slot declarations and implementer
/// bindings through type-argument substitution.
/// </summary>
public class AssociatedTypeModelTests
{
    /// <summary>
    /// An entity's <c>relates … as Iter</c> binding, when generic over <c>T</c>, must substitute
    /// <c>T</c> in the bound type when the entity is monomorphized.
    /// </summary>
    [Fact]
    public void Entity_CreateInstance_SubstitutesAssociatedTypeBinding()
    {
        var concrete = new RecordTypeInfo(name: "S64");
        var paramT = new GenericParameterTypeInfo(name: "T");

        var def = new EntityTypeInfo(name: "Box")
        {
            GenericParameters = ["T"],
            // relates SomeEmitter[T] as Iter  →  bind Iter to a type that mentions T.
            AssociatedTypeBindings = new() { ["Iter"] = paramT }
        };

        var instance = (EntityTypeInfo)def.CreateInstance(typeArguments: [concrete]);

        Assert.True(condition: instance.AssociatedTypeBindings.ContainsKey(key: "Iter"));
        // T was substituted with the concrete argument.
        Assert.Same(expected: concrete, actual: instance.AssociatedTypeBindings["Iter"]);
    }

    /// <summary>
    /// A protocol's <c>relates Iter obeys …</c> slot must carry through monomorphization with its
    /// constraint substituted.
    /// </summary>
    [Fact]
    public void Protocol_CreateInstance_CarriesAssociatedTypeSlot()
    {
        var concrete = new RecordTypeInfo(name: "Text");
        var paramT = new GenericParameterTypeInfo(name: "T");

        var def = new ProtocolTypeInfo(name: "Sequence")
        {
            GenericParameters = ["T"],
            AssociatedTypes = [new AssociatedTypeSlot(name: "Iter") { Constraint = paramT }]
        };

        var instance = (ProtocolTypeInfo)def.CreateInstance(typeArguments: [concrete]);

        Assert.Single(collection: instance.AssociatedTypes);
        Assert.Equal(expected: "Iter", actual: instance.AssociatedTypes[index: 0].Name);
        Assert.Same(expected: concrete, actual: instance.AssociatedTypes[index: 0].Constraint);
    }

    /// <summary>
    /// A projection <c>S/Iter</c> records its base and slot name.
    /// </summary>
    [Fact]
    public void Projection_RecordsBaseAndSlot()
    {
        var paramS = new GenericParameterTypeInfo(name: "S");
        var projection = new AssociatedProjectionTypeInfo(baseType: paramS, slotName: "Iter");

        Assert.Same(expected: paramS, actual: projection.Base);
        Assert.Equal(expected: "Iter", actual: projection.SlotName);
        Assert.Equal(expected: "S/Iter", actual: projection.Name);
    }
}