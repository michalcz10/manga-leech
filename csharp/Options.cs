namespace MangaLeech.Core;

internal sealed class Options
{
    public string? Source { get; private set; }
    public string? Chapter { get; private set; }
    public string Output { get; private set; } = "downloads";
    public string UserAgent { get; private set; } = "MangaLeech.CSharp/0.2";
    public bool List { get; private set; }
    public bool Newest { get; private set; }
    public bool Oldest { get; private set; }
    public bool Force { get; private set; }
    public bool Rebuild { get; private set; }
    public bool Manifest { get; private set; }
    public bool Help { get; private set; }
    public bool DeleteImageCache { get; private set; }
    public string Language { get; private set; } = "en";
    public int Workers { get; private set; } = 8;
    public int PdfWorkers { get; private set; } = 2;
    public int ChapterWorkers { get; private set; } = 2;
    public int MemoryLimitMiB { get; private set; } = 512;
    public int? Limit { get; private set; }
    public int? JpegQuality { get; private set; }
    public double Timeout { get; private set; } = 60;
    public double Delay { get; private set; } = 0.5;
    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing argument value.");
            int Integer() => int.Parse(Value(), CultureInfo.InvariantCulture);
            double Number() => double.Parse(Value(), CultureInfo.InvariantCulture);
            switch (args[i])
            {
                case "-h":
                case "--help":
                    o.Help = true;
                    break;
                case "--chapter":
                    o.Chapter = Value();
                    break;
                case "--language":
                    o.Language = Value().ToLowerInvariant();
                    if (!Regex.IsMatch(o.Language, "^[a-z]{2}(?:-[a-z]{2})?$"))
                        throw new ArgumentException("--language requires a language code (e.g. en, pt-br).");
                    break;
                case "-o":
                case "--output":
                    o.Output = Value();
                    break;
                case "--user-agent":
                    o.UserAgent = Value();
                    break;
                case "--list":
                    o.List = true;
                    break;
                case "--limit":
                    o.Limit = Integer();
                    break;
                case "--oldest-first":
                    o.Oldest = true;
                    break;
                case "--newest-first":
                    o.Newest = true;
                    break;
                case "--force":
                    o.Force = true;
                    break;
                case "--rebuild":
                    o.Rebuild = true;
                    break;
                case "--write-manifest":
                    o.Manifest = true;
                    break;
                case "--jpeg-quality":
                    o.JpegQuality = Integer();
                    break;
                case "--workers":
                    o.Workers = Integer();
                    break;
                case "--pdf-workers":
                    o.PdfWorkers = Integer();
                    break;
                case "--memory-limit-mib":
                    o.MemoryLimitMiB = Integer();
                    break;
                case "--chapter-workers":
                    o.ChapterWorkers = Integer();
                    break;
                case "--delete-image-cache":
                    o.DeleteImageCache = true;
                    break;
                case "--timeout":
                    o.Timeout = Number();
                    break;
                case "--delay":
                    o.Delay = Number();
                    break;
                case "--pdf-tasks-per-child":
                    throw new ArgumentException("PDF workers use threads. Omit --pdf-tasks-per-child.");
                default:
                    if (args[i].StartsWith('-') || o.Source != null)
                        throw new ArgumentException($"Unknown argument: {args[i]}");
                    o.Source = args[i];
                    break;
            }
        }

        if (o.Help)
            return o;
        if ((o.Source == null) == (o.Chapter == null))
            throw new ArgumentException("Specify a series/feed URL or --chapter URL.");
        if (o.Newest && o.Oldest)
            throw new ArgumentException("Select only one sort direction.");
        if (!double.IsFinite(o.Timeout) || o.Timeout <= 0 || o.Timeout > (uint.MaxValue - 1) / 1000.0 || !double.IsFinite(o.Delay) || o.Delay < 0 || o.Delay > TimeSpan.MaxValue.TotalSeconds / 2 || o.Workers is < 1 or > 1024 || o.PdfWorkers is < 1 or > 1024 || o.ChapterWorkers is < 1 or > 1024 || o.MemoryLimitMiB is < 64 or > 32768 || o.Limit is <= 0)
            throw new ArgumentException("Invalid timeout, delay, workers, chapter-workers, pdf-workers or limit.");
        if (o.JpegQuality is < 1 or > 95)
            throw new ArgumentException("jpeg-quality must be between 1 and 95.");
        return o;
    }
}
