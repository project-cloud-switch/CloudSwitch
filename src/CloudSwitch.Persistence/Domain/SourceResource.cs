namespace CloudSwitch.Persistence.Domain;

public sealed record SourceResource
{
    public string Type { get; }
    public string FolderId { get; }
    public string ResourceId { get; }

    public SourceResource(string type, string folderId, string resourceId)
    {
        Type = DomainGuard.Text(type, nameof(Type), 64);
        FolderId = DomainGuard.Text(folderId, nameof(FolderId), 128);
        ResourceId = DomainGuard.Text(resourceId, nameof(ResourceId), 128);
    }
}