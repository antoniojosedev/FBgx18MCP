namespace GxMcp.Gateway
{
    public sealed record KbHandle(string Alias, string Path)
    {
        public string NormalizedAlias => Alias.Trim().ToLowerInvariant();
    }
}
