namespace MangaLeech.Core;

// Native equivalent of MangaFire's dynamicEncrypt function. Verified against
// polyfill-tlfroa-Bq-bHCd3.js (SHA-256 2969ee2d175e6da00d28a808680d12bb796f4e537fb7609d0afa42c89e4863f3).
// These are protocol constants, not user credentials. If MangaFire changes the
// signing protocol, update the rounds and the independent reference vectors.
internal static class MangaFireSigner
{
    private sealed record Round(byte[] Table, byte[] Key, byte InitialFeedback);

    private static readonly Round[] Rounds =
    [
        new(
            Convert.FromBase64String("yINlmUNho8VYJT+ibTIP+9ESiULpVEtMOoD6U6lRE0R/xwXo/Xp9NrUgC4cw/Lmo33vUyjUE40kUoEWIr/fxfNNcq2s79ShQ5NhNrFnJ4hXPwOu/SuXzIbuTQKGFvfm08E9jvCfqAtoDqvQq3dVWPQFmJjgvkISBeXY3BgANR+yVnjGbcxZ47d6kLNfZPIayTq3/YGySb1KuVZodWp/WGNAO5pfMcpaK53Hhs0allBszaMaxuouOwdxbwgxIw6YunSsXjI05Yi0j9j4eHKfSXR8Ifo/Od+8iamRfCXTyvm7NGRGYdcQ0ywcK/u6RXhrbcCm4t2eCtrDgQVecJGkQ+A=="),
            Convert.FromBase64String("0Ec58JOY3uBzJK9m3zqIOpdlF7UFiax9DmA="), 90),
        new(
            Convert.FromBase64String("IUFltCxD3Oc2cwCgkJffthaOg9cgPUb0LgW6H/VtfcF0kc5F25t+aWj6JH9VOhOaY0rAFdUxlDnl5BLNvwEJvQtP5qcw7vdb/K+chnbwnspSHT8mz5lqwz41TezG0hkO06FTjJZhsyNuFLDpD2ZZxQj/QIRcF90zpmQ7Byu483WsQqUE0C342HL+JXngRB6fRzxRyVTaKu83h7UYTJ0QMt6ixFh6S3F8gqkKwrGTL3jHNBsD45UnifK8+RGtishQV2K3rujLKEkiZxpr2dYcudFW4oFsDKhad3CLBvuyTqsCo4B7mL5IKQ1vXo/MOOvq1I1d8ar9X6Ttu5KF4fZgiA=="),
            Convert.FromBase64String("AAdjb1iPY8CiDmq9H34tKTBF8a3oDQ=="), 53),
        new(
            Convert.FromBase64String("NQHlu1/wVO5EmkwQymF810qqY2xG1k2obcas4Z9mCsPEIFl9pRIjFxbJ7ybMHbBckT5Ton85E0FOeHezbh/mjlEYpmpnlXOS8dgrqeq2KfxImTh1YK9y0PeMNhzA1OQzSY9brYOJq/l2QnE/hwOeZIhPixVSKIUlDb5vLcH6RWKxkIEMuP0bDwIqQ71AJJaEaMJL7A6YtyIwoRT+L5v4aZzodN/0+3nOGsfblFjgxSfPzVDjNFeNl5P26+kEC/8AHgdrpAbt3hHz3HrRN1Y6e+JHgF7ncFWnoF0y3THL1S71WgWGCa6KtSzTCCG58n68nTyj2T3Sshk7utqCtMi/ZQ=="),
            Convert.FromBase64String("DELOJgPsVaCcblDtTGMdHzM="), 186),
    ];

    internal static string Sign(string path, IReadOnlyDictionary<string, string> parameters)
    {
        // The protocol signs decoded values, sorted by UTF-16 key order. URL
        // encoding is applied separately when the HTTP query string is built.
        string query = string.Join("&", parameters
            .Where(pair => pair.Key != "vrf" && pair.Value != null)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + pair.Value));
        return Encode(query.Length == 0 ? path : path + "?" + query);
    }

    internal static string Encode(string input)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(input);
        foreach (var round in Rounds)
        {
            byte feedback = round.InitialFeedback;
            for (int i = 0; i < bytes.Length; i++)
            {
                // Each stage substitutes the input XORed with a cycling key and
                // the previous OUTPUT byte. Reset feedback between stages.
                byte output = round.Table[bytes[i] ^ round.Key[i % round.Key.Length] ^ feedback];
                bytes[i] = feedback = output;
            }
        }
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
