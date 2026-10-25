namespace SchoolPdfSorter.Support;

/// <summary>
/// Holds the scrambled support details. The release script fills these from a local
/// <c>support.local.json</c> that is never committed. A plain checkout has no details, so
/// the support section stays hidden instead of showing a button that does nothing.
/// </summary>
internal static partial class SupportPayload
{
    internal static (string Payload, string Seal) Values()
    {
        var payload = "";
        var seal = "";

        Fill(ref payload, ref seal);

        return (payload, seal);
    }

    static partial void Fill(ref string payload, ref string seal);
}
