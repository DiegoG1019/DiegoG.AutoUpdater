namespace DiegoG.AutoUpdater.UpdateSources;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class UpdateSourceAttribute : Attribute
{
    public string SourceName { get; }

    public UpdateSourceAttribute(string sourceName)
    {
        SourceName = sourceName ?? throw new ArgumentNullException(nameof(sourceName));
    }
}
