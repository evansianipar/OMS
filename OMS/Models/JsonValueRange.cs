namespace OMS.Models;

public sealed class JsonValueRange
{
    public string Range { get; init; } = string.Empty;
    public IList<IList<object>> Values { get; init; } = [];
}
