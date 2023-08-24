using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MeaMod.DNS.Model;
using MeaMod.DNS.Multicast;

namespace OscQueryLibrary;

public class OscQueryServer
{
    private readonly ushort _httpPort; // TODO: remove when switching httpServer library for proper random port support
    private readonly string _ipAddress;
    private readonly ushort _oscPort;
    private const string OscHttpServiceName = "_oscjson._tcp";
    private const string OscUdpServiceName = "_osc._udp";
    private readonly HttpListener _httpListener;
    private readonly MulticastService _multicastService;
    private readonly ServiceDiscovery _serviceDiscovery;
    private readonly string _serviceName;
    private object? _hostInfo;
    private object? _queryData;

    private static readonly HashSet<string> FoundServices = new();
    private static IPEndPoint? _lastVrcHttpServer;
    private static event Action<Dictionary<string, object?>>? ParameterUpdate;
    private static readonly Dictionary<string, object?> ParameterList = new();

    public OscQueryServer(string serviceName, string ipAddress, ushort oscPort,
        Action<Dictionary<string, object?>>? parameterUpdate = null)
    {
        _serviceName = serviceName;
        _ipAddress = ipAddress;
        _oscPort = oscPort;
        _httpPort = FindAvailableTcpPort();
        ParameterUpdate = parameterUpdate;
        SetupJsonObjects();
        // ignore our own service
        FoundServices.Add($"{_serviceName.ToLower()}.{OscHttpServiceName}.local:{_httpPort}");

        // HTTP Server
        _httpListener = new HttpListener();
        var prefix = $"http://{_ipAddress}:{_httpPort}/";
        _httpListener.Prefixes.Add(prefix);
        _httpListener.Start();
        _httpListener.BeginGetContext(OnHttpRequest, null);
        Debug.WriteLine($"OSCQueryHttpServer: Listening at {prefix}");

        // mDNS
        _multicastService = new MulticastService
        {
            UseIpv6 = false,
            IgnoreDuplicateMessages = true
        };
        _serviceDiscovery = new ServiceDiscovery(_multicastService);
        ListenForServices();
        _multicastService.Start();
        AdvertiseOscQueryServer();
    }

    private void AdvertiseOscQueryServer()
    {
        var httpProfile =
            new ServiceProfile(_serviceName, OscHttpServiceName, _httpPort,
                new[] { IPAddress.Parse(_ipAddress) });
        var oscProfile =
            new ServiceProfile(_serviceName, OscUdpServiceName, _oscPort,
                new[] { IPAddress.Parse(_ipAddress) });
        _serviceDiscovery.Advertise(httpProfile);
        _serviceDiscovery.Advertise(oscProfile);
    }

    private void ListenForServices()
    {
        _multicastService.NetworkInterfaceDiscovered += (_, args) =>
        {
            Debug.WriteLine("OSCQueryMDNS: Network interface discovered");
            _multicastService.SendQuery($"{OscHttpServiceName}.local");
            _multicastService.SendQuery($"{OscUdpServiceName}.local");
        };

        _multicastService.AnswerReceived += OnAnswerReceived;
    }

    private static void OnAnswerReceived(object? sender, MessageEventArgs args)
    {
        var response = args.Message;
        try
        {
            foreach (var record in response.AdditionalRecords.OfType<SRVRecord>())
            {
                var domainName = record.Name.Labels;
                var instanceName = domainName[0];
                var type = domainName[2];
                var serviceId = $"{record.CanonicalName}:{record.Port}";
                if (type == "_udp")
                    continue; // ignore UDP services

                if (record.TTL == TimeSpan.Zero)
                {
                    Debug.WriteLine($"OSCQueryMDNS: Goodbye message from {record.CanonicalName}");
                    FoundServices.Remove(serviceId);
                    continue;
                }

                if (FoundServices.Contains(serviceId))
                    continue;

                var ips = response.AdditionalRecords.OfType<ARecord>().Select(r => r.Address);
                // TODO: handle more than one IP address
                var ipAddress = ips.FirstOrDefault();
                FoundServices.Add(serviceId);
                Debug.WriteLine($"OSCQueryMDNS: Found service {serviceId} {instanceName} {ipAddress}:{record.Port}");

                if (instanceName.StartsWith("VRChat-Client-") && ipAddress != null)
                {
                    _lastVrcHttpServer = new IPEndPoint(ipAddress, record.Port);
                    FetchJsonFromVrc(ipAddress, record.Port).GetAwaiter();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to parse from {args.RemoteEndPoint}: {ex.Message}");
        }
    }

    private static bool _fetchInProgress;

    private static async Task FetchJsonFromVrc(IPAddress ipAddress, int port)
    {
        if (_fetchInProgress) return;
        _fetchInProgress = true;
        var url = $"http://{ipAddress}:{port}/";
        Debug.WriteLine($"OSCQueryHttpClient: Fetching new parameters from {url}");
        var response = string.Empty;
        var client = new HttpClient();
        try
        {
            response = await client.GetStringAsync(url);
            var rootNode = JsonSerializer.Deserialize<OscQueryModels.RootNode>(response);
            if (rootNode?.CONTENTS?.avatar?.CONTENTS?.parameters?.CONTENTS == null)
            {
                Console.WriteLine("OSCQueryHttpClient: Error no parameters found");
                return;
            }

            ParameterList.Clear();
            foreach (var node in rootNode.CONTENTS.avatar.CONTENTS.parameters.CONTENTS!.Values)
            {
                RecursiveParameterLookup(node);
            }

            ParameterUpdate?.Invoke(ParameterList);
        }
        catch (HttpRequestException ex)
        {
            _lastVrcHttpServer = null;
            ParameterList.Clear();
            ParameterUpdate?.Invoke(ParameterList);
            Console.WriteLine($"OSCQueryHttpClient: Error {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"OSCQueryHttp: Error {ex.Message}\n{response}");
        }
        finally
        {
            _fetchInProgress = false;
        }
    }

    private static void RecursiveParameterLookup(OscQueryModels.Node node)
    {
        if (node.CONTENTS == null)
        {
            ParameterList.Add(node.FULL_PATH, node.VALUE?[0]);
            return;
        }

        foreach (var subNode in node.CONTENTS.Values)
        {
            RecursiveParameterLookup(subNode);
        }
    }

    private async void OnHttpRequest(IAsyncResult result)
    {
        var context = _httpListener.EndGetContext(result);
        _httpListener.BeginGetContext(OnHttpRequest, null);
        var request = context.Request;
        var response = context.Response;
        var path = request.Url?.AbsolutePath;
        if (path == null || request.RawUrl == null)
            return;

        if (!request.RawUrl.Contains("HOST_INFO") && path != "/")
        {
            response.StatusCode = 404;
            response.StatusDescription = "Not Found";
            response.Close();
            return;
        }

        Debug.WriteLine($"OSCQueryHttp request: {path}");

        var json = JsonSerializer.Serialize(request.RawUrl.Contains("HOST_INFO") ? _hostInfo : _queryData);
        // JsonConvert.SerializeObject(request.RawUrl.Contains("HOST_INFO") ? _hostInfo : _queryData);
        response.Headers.Add("pragma:no-cache");
        response.ContentType = "application/json";
        var buffer = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer);
        response.OutputStream.Close();
    }

    public static async Task GetParameters()
    {
        if (_lastVrcHttpServer == null)
            return;

        await FetchJsonFromVrc(_lastVrcHttpServer.Address, _lastVrcHttpServer.Port);
    }

    private ushort FindAvailableTcpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Parse(_ipAddress), port: 0));
        ushort port = 0;
        if (socket.LocalEndPoint != null)
            port = (ushort)((IPEndPoint)socket.LocalEndPoint).Port;
        return port;
    }

    private void SetupJsonObjects()
    {
        _queryData = new
        {
            DESCRIPTION = "",
            FULL_PATH = "/",
            ACCESS = 0,
            CONTENTS = new
            {
                avatar = new
                {
                    FULL_PATH = "/avatar",
                    ACCESS = 2
                }
            }
        };

        _hostInfo = new
        {
            NAME = _serviceName,
            OSC_PORT = (int)_oscPort,
            OSC_IP = _ipAddress,
            OSC_TRANSPORT = "UDP",
            EXTENSIONS = new
            {
                ACCESS = true,
                CLIPMODE = true,
                RANGE = true,
                TYPE = true,
                VALUE = true
            }
        };
    }

    private void Dispose()
    {
        _multicastService.Dispose();
        _serviceDiscovery.Dispose();
        _httpListener.Stop();
        _httpListener.Close();
    }

    ~OscQueryServer()
    {
        Dispose();
    }
}