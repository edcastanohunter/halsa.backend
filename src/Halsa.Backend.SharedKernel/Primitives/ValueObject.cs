namespace Halsa.Backend.SharedKernel.Primitives;

/// <summary>
/// An immutable descriptive object with no identity: two value objects with the same components are
/// the same value. Prefer <c>record</c> types; this base exists for value objects that need custom
/// component semantics (collections, tolerances, normalisation).
/// </summary>
public abstract class ValueObject : IEquatable<ValueObject>
{
    protected abstract IEnumerable<object?> GetAtomicValues();

    public bool Equals(ValueObject? other) =>
        other is not null && other.GetType() == GetType() && GetAtomicValues().SequenceEqual(other.GetAtomicValues());

    public override bool Equals(object? obj) => obj is ValueObject valueObject && Equals(valueObject);

    public override int GetHashCode()
    {
        var hashCode = new HashCode();

        foreach (object? value in GetAtomicValues())
        {
            hashCode.Add(value);
        }

        return hashCode.ToHashCode();
    }

    public static bool operator ==(ValueObject? left, ValueObject? right) => Equals(left, right);

    public static bool operator !=(ValueObject? left, ValueObject? right) => !Equals(left, right);
}
