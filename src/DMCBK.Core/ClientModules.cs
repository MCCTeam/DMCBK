namespace DMCBK.Core;

/// <summary>Optional services owned by one client instance.</summary>
public sealed partial class Client
{
    /// <summary>This client's manual sources and page cache; plugin registrations never cross clients.</summary>
    public Manual.ManualCatalog Manuals { get; } = new();

    /// <summary>The optional command dispatcher. Throws when Commands is not attached.</summary>
    public Commands.ICommandDispatcher Commands => GetModule<Commands.ICommandDispatcher>();

    /// <summary>The culture for this client, independent of other clients in the process.</summary>
    public System.Globalization.CultureInfo UiCulture { get; set; } = System.Globalization.CultureInfo.CurrentUICulture;

    /// <summary>The application's explicit identity.</summary>
    public HostApplication Application { get; internal set; } = new("dmcbk", DmcbkVersion.Current, new HashSet<string>());

    /// <summary>The host and attached module capabilities.</summary>
    public IReadOnlySet<string> AvailableCapabilities
    {
        get { lock (_moduleGate) return _moduleCapabilities.Concat(Application.Capabilities).ToHashSet(StringComparer.Ordinal); }
    }

    private readonly HashSet<string> _moduleCapabilities = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, object> _modules = [];
    private readonly List<object> _moduleLifetimes = [];
    private readonly object _moduleGate = new();
    private bool _modulesDisposed;

    /// <summary>Gets an attached optional module or reports its unavailable capability.</summary>
    public T GetModule<T>() where T : class
        => TryGetModule<T>(out T? module) ? module! : throw new NotSupportedException(typeof(T).FullName);

    /// <summary>Tries to get an attached optional module.</summary>
    public bool TryGetModule<T>(out T? module) where T : class
    {
        lock (_moduleGate)
        {
            module = _modules.GetValueOrDefault(typeof(T)) as T ?? _modules.Values.OfType<T>().SingleOrDefault();
            return module is not null;
        }
    }

    /// <summary>Attaches a module and transfers its disposal to the client.</summary>
    public void AttachModule<T>(T module, string? capability = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(module);
        lock (_moduleGate)
        {
            ObjectDisposedException.ThrowIf(_modulesDisposed, this);
            if (!_modules.TryAdd(typeof(T), module))
                throw new InvalidOperationException(typeof(T).FullName);
            _moduleLifetimes.Add(module);
            if (capability is not null) _moduleCapabilities.Add(capability);
        }
    }

    private async ValueTask DisposeModulesAsync()
    {
        object[] modules;
        lock (_moduleGate)
        {
            if (_modulesDisposed) return;
            _modulesDisposed = true;
            modules = _moduleLifetimes.ToArray();
            _moduleLifetimes.Clear();
        }
        List<Exception>? failures = null;
        foreach (object module in modules.Reverse())
        {
            try
            {
                if (module is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                else if (module is IDisposable disposable)
                    disposable.Dispose();
            }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        lock (_moduleGate) { _modules.Clear(); _moduleCapabilities.Clear(); }
        if (failures is not null) throw new AggregateException(failures);
    }
}

/// <summary>Builds optional services after constructing a client.</summary>
public sealed partial class ClientBuilder
{
    private HostApplication _application = new("dmcbk", DmcbkVersion.Current, new HashSet<string>());

    /// <summary>Supplies the application identity and additional host capabilities.</summary>
    public ClientBuilder UseApplication(HostApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentException.ThrowIfNullOrWhiteSpace(application.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(application.Version);
        _application = application with { Capabilities = application.Capabilities.ToHashSet(StringComparer.Ordinal) };
        return this;
    }

    private readonly List<Action<Client>> _moduleInstallers = [];
    private readonly HashSet<Type> _moduleTypes = [];

    /// <summary>Adds one module factory. The factory runs independently for each built client.</summary>
    public ClientBuilder UseModule<T>(Func<Client, T> factory, string? capability = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (!_moduleTypes.Add(typeof(T))) throw new InvalidOperationException(typeof(T).FullName);
        _moduleInstallers.Add(client => client.AttachModule(factory(client), capability));
        return this;
    }
}
