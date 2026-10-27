using System.Text.Json;
using Jint;

namespace MangaLeech.Core;

// Only the site's signing module runs here. Jint is an in-process .NET interpreter:
// no browser, Node.js process, DOM rendering, network APIs or CLR access are exposed.
internal sealed class MangaFireSigner : IDisposable
{
    private readonly Engine engine;
    private readonly object gate = new();
    private readonly CancellationToken cancellationToken;

    internal MangaFireSigner(string source, double timeout, CancellationToken cancellationToken)
    {
        this.cancellationToken = cancellationToken;
        if (source.Length > 1_000_000)
            throw new InvalidDataException("MangaFire signing module exceeds the size limit.");
        engine = new Engine(options =>
        {
            options.TimeoutInterval(TimeSpan.FromSeconds(Math.Min(timeout, 30)))
                .LimitMemory(128_000_000).MaxStatements(20_000_000).CancellationToken(cancellationToken);
            options.Host.StringCompilationAllowed = false;
        });
        try
        {
            engine.SetValue("__base64Encode", (Func<string, string>)(value => Convert.ToBase64String(Encoding.Latin1.GetBytes(value))));
            engine.SetValue("__base64Decode", (Func<string, string>)(value => Encoding.Latin1.GetString(Convert.FromBase64String(value))));
            engine.Execute(Bootstrap);
            // Module exports are irrelevant: the module also exposes its signer on
            // globalThis. All other site modules and inline scripts remain unexecuted.
            source = Regex.Replace(source, @"\bexport\s*\{[^}]*\}\s*;?\s*$", "");
            engine.Execute(source);
            if (!engine.Evaluate("typeof getProtectionToken === 'function'").AsBoolean())
                throw new InvalidDataException("MangaFire signing module has no getProtectionToken function.");
        }
        catch (Jint.Runtime.ExecutionCanceledException)
        {
            engine.Dispose();
            throw new OperationCanceledException(cancellationToken);
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    internal string Sign(string path, IReadOnlyDictionary<string, string> parameters)
    {
        // An engine is not thread-safe. Serialize just the small signing step;
        // HTTP requests and image downloads can still overlap normally.
        lock (gate)
        {
            try
            {
                var arguments = engine.Evaluate("(" + JsonSerializer.Serialize(parameters) + ")");
                var result = engine.Invoke("getProtectionToken", path, arguments);
                if (!result.IsString() || result.AsString().Length == 0)
                    throw new InvalidDataException("MangaFire did not generate an API signature.");
                return result.AsString();
            }
            catch (Jint.Runtime.ExecutionCanceledException)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }

    public void Dispose() => engine.Dispose();

    private const string Bootstrap = """
        var window = globalThis, self = globalThis;
        var navigator = {appCodeName:'Mozilla', webdriver:false, userAgent:'Mozilla/5.0', platform:'Linux x86_64', languages:['en']};
        var location = {hostname:'mangafire.to', href:'https://mangafire.to/', protocol:'https:', host:'mangafire.to', pathname:'/'};
        var localStorage = {getItem:()=>null, setItem(){}, removeItem(){}}, sessionStorage = localStorage;
        var document = {cookie:'', createElement:()=>({style:{}, setAttribute(){}, appendChild(){}}),
            querySelector:()=>null, querySelectorAll:()=>[], addEventListener(){}, documentElement:{}, head:{appendChild(){}}};
        var console = {log(){}, warn(){}, error(){}};
        function btoa(value){return __base64Encode(value)}
        function atob(value){return __base64Decode(value)}
        function setTimeout(){return 0} function setInterval(){return 0}
        function clearTimeout(){} function clearInterval(){} function addEventListener(){}
        class TextEncoder {
            encode(value){const bytes=unescape(encodeURIComponent(value));return Uint8Array.from(bytes,c=>c.charCodeAt(0));}
        }
        class TextDecoder {
            decode(bytes){let value='';for(const byte of bytes)value+=String.fromCharCode(byte);return decodeURIComponent(escape(value));}
        }
        """;
}
