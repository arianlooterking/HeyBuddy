using Clicky.Core;
using Clicky.Connectors;
using Clicky.Runtime;
using Clicky.Windows.Native;
using Clicky.Windows.Speech;

namespace Clicky.Windows;

public sealed class AppServices : IAsyncDisposable
{
    public AppSettings Settings { get; } = AppSettings.Load();
    public AppStore Store { get; } = new();
    public KnowledgeStore Knowledge { get; } = new();
    public DpapiCredentialStore Credentials { get; } = new();
    public ModelProviderFactory Factory
    {
        get;
    }
    public ConnectorService Connectors
    {
        get;
    }
    public DocumentTools Documents
    {
        get;
    }
    public WindowsDesktopTools Desktop { get; } = new();
    public ScreenCaptureService Capture { get; } = new();
    public SpeechService Speech
    {
        get;
    }
    public AgentRunner Agents
    {
        get;
    }
    private readonly SemaphoreSlim modelGate = new(1, 1);
    public AppServices()
    {
        AppPaths.Ensure();
        Factory = new(Settings, Credentials);
        Connectors = new(Credentials);
        Documents = new(Settings);
        Speech = new(Settings);
        Agents = new(Store);
        Store.PruneHistory(Settings.HistoryRetentionDays);
    }
    public IModelProvider Provider() => new QueuedModelProvider(Factory.Create(), modelGate);
    public IReadOnlyList<IToolExecutor> Tools() => [Desktop, Documents, Connectors];
    public async ValueTask DisposeAsync()
    {
        Agents.CancelAll();
        Speech.Dispose();
        Documents.Dispose();
        await Connectors.DisposeAsync();
        await Factory.DisposeAsync();
    }
}
