#nullable enable

namespace Sollatek.DataSync.Storage.Relational;

public enum RelationalColumnRole
{
    PrimaryKey,
    Scalar,
    ReferenceFlatValue,
    ReferenceForeignKey
}
