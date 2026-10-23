using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace IPRO.Utility;

// 595: one question put straight to a zone's own name servers, over TCP (RFC 7766), and the answer read.
//
// Why not the public resolvers the domain check asks (dns.google, cloudflare-dns.com)? They remember a "no such
// record" for the zone's negative TTL: ten minutes at GoDaddy (SOA minimum 600, read on drhug.ca and
// maison1850.com on 2026-10-21), three hours elsewhere (ops/domain-switch/dns-check.sh). A setup guide that asks
// every twenty seconds always asks before the adviser has saved the record, so through a resolver the step would
// sit unticked for that long after it was done. The zone's own servers answer what is there now.
//
// The answer comes from a server the domain's owner chose, so it is read as untrusted bytes: every length is
// checked against the message, compressed names may jump at most 16 times and are at most 255 characters, and
// anything malformed is no answer at all (null), never an exception. Only public addresses are asked
// (PublicHostGuard), on port 53, with a short timeout, and the answer is at most 16 KB.
public static class ZoneDns
{
    public const ushort A = 1;
    public const ushort NS = 2;
    public const ushort CNAME = 5;
    public const ushort SOA = 6;
    public const ushort MX = 15;
    public const ushort TXT = 16;
    public const ushort AAAA = 28;
    public const ushort DS = 43;      // asked of the public resolvers only: is the zone signed?

    public const int NoError = 0;
    public const int NameError = 3;   // NXDOMAIN: no such name
    internal const int ServerFailure = 2;

    internal const int MaxMessage = 16 * 1024;
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public sealed record Record(ushort Type, string Name, string Data);

    // Rcode NoError or NameError (Usable), or another rcode (REFUSED, SERVFAIL: this server gives no answer of its own,
    // with no records); Records holds the answer section's A, AAAA, CNAME, NS, MX and TXT records (TXT strings joined
    // into one value, names lower-case without the final dot).
    public sealed record Answer(int Rcode, bool Authoritative, IReadOnlyList<Record> Records)
    {
        public bool Usable => Rcode is NoError or NameError;

        public IEnumerable<string> Values(ushort type, string name) =>
            Records.Where(r => r.Type == type && string.Equals(r.Name, Normalize(name), StringComparison.Ordinal)).Select(r => r.Data);
    }

    // Several questions to one server. Reached is false only when the server could not be reached at all (no
    // connection, or no answer at all before the timeout): the one thing a caller may hold against the server.
    public sealed record Batch(bool Reached, IReadOnlyList<Answer?> Answers);

    // Test seam (PublicHostGuard.ResolveHook's shape): replaces the network question.
    // Internal: nothing in the apps may switch the zone's servers off or replace them (595's review, L15).
    internal static Func<IPAddress, string, ushort, CancellationToken, Task<Answer?>>? QueryHook { get; set; }

    public static async Task<Answer?> QueryAsync(IPAddress server, string name, ushort type, CancellationToken cancellationToken = default) =>
        (await QueryManyAsync(server, new[] { (name, type) }, cancellationToken)).Answers[0];

    // The questions over ONE connection (RFC 7766: queries pipelined, answers matched by id), so a look costs one
    // connection per name server rather than one per question (595's review, L-k: App Service allows a limited number of
    // outbound ports per destination). A server that answers one and closes gets the rest one at a time.
    public static async Task<Batch> QueryManyAsync(IPAddress server, IReadOnlyList<(string Name, ushort Type)> questions, CancellationToken cancellationToken = default)
    {
        var answers = new Answer?[questions.Count];
        if (QueryHook is { } hook)
        {
            for (var i = 0; i < questions.Count; i++) answers[i] = await hook(server, questions[i].Name, questions[i].Type, cancellationToken);
            return new Batch(questions.Count == 0 || answers.Any(a => a != null), answers);
        }
        if (PublicHostGuard.IsBlockedAddress(server)) return new Batch(false, answers);

        var pending = new Dictionary<ushort, int>();
        var framed = new List<byte>();
        for (var i = 0; i < questions.Count; i++)
        {
            ushort id;
            do { id = BinaryPrimitives.ReadUInt16BigEndian(RandomNumberGenerator.GetBytes(2)); } while (pending.ContainsKey(id));
            var query = BuildQuery(id, questions[i].Name, questions[i].Type);
            if (query == null) continue;                         // a name that cannot be asked: no answer, nobody's fault
            pending[id] = i;
            var length = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)query.Length);
            framed.AddRange(length);
            framed.AddRange(query);
        }
        if (pending.Count == 0) return new Batch(true, answers);

        var reached = false;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(Timeout);
            try
            {
                using var client = new TcpClient(server.AddressFamily);
                await client.ConnectAsync(server, 53, timeout.Token);
                await using var stream = client.GetStream();
                await stream.WriteAsync(framed.ToArray(), timeout.Token);
                reached = await ReadAnswersAsync(stream, pending, answers, timeout.Token);
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) throw;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();

        // Answered some and then closed (or stopped answering): the rest one at a time, each on its own connection.
        if (reached && pending.Count > 0 && questions.Count > 1)
        {
            foreach (var index in pending.Values.ToList())
            {
                answers[index] = (await QueryManyAsync(server, new[] { questions[index] }, cancellationToken)).Answers[0];
            }
        }
        return new Batch(reached, answers);
    }

    // The answers on one connection, each matched to its question by id. True once the server has said anything at all:
    // a server that answers, even with something unusable, is not unreachable, and only an unreachable one is left out
    // for everyone (595's third review, L-1: an adviser can make their own zone's answer too large). An answer over 16 KB
    // is read and set aside as no answer of its own; a length too short to be a message ends the reading.
    internal static async Task<bool> ReadAnswersAsync(Stream stream, Dictionary<ushort, int> pending, Answer?[] answers, CancellationToken cancellationToken)
    {
        var reached = false;
        var lengthBytes = new byte[2];
        try
        {
            while (pending.Count > 0)
            {
                await stream.ReadExactlyAsync(lengthBytes, cancellationToken);
                reached = true;
                var length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
                if (length < 12) break;
                var message = new byte[length];
                await stream.ReadExactlyAsync(message, cancellationToken);
                var id = BinaryPrimitives.ReadUInt16BigEndian(message);
                if (pending.Remove(id, out var index))
                    answers[index] = length > MaxMessage ? new Answer(ServerFailure, false, Array.Empty<Record>()) : Parse(message, id);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // Closed or silent: what came before stands.
        }
        return reached;
    }

    // A standard query for one name and type, class IN, recursion not asked for (these servers answer for their
    // own zone only). Null for a name that cannot be asked (a label over 63 characters, a name over 253).
    public static byte[]? BuildQuery(ushort id, string name, ushort type)
    {
        var labels = Normalize(name).Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length == 0) return null;
        var body = new List<byte>(512);
        var header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0), id);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), 1);   // one question
        body.AddRange(header);
        var total = 0;
        foreach (var label in labels)
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length > 63) return null;
            total += bytes.Length + 1;
            body.Add((byte)bytes.Length);
            body.AddRange(bytes);
        }
        if (total > 254) return null;
        body.Add(0);
        var tail = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(tail.AsSpan(0), type);
        BinaryPrimitives.WriteUInt16BigEndian(tail.AsSpan(2), 1);       // class IN
        body.AddRange(tail);
        return body.ToArray();
    }

    // The answer to query `expectedId`, or null when the message is not one (wrong id, not a response, truncated,
    // malformed). An rcode other than NOERROR / NXDOMAIN comes back with no records, and is not Usable.
    public static Answer? Parse(ReadOnlySpan<byte> message, ushort expectedId)
    {
        try
        {
            if (message.Length < 12 || message.Length > MaxMessage) return null;
            if (BinaryPrimitives.ReadUInt16BigEndian(message) != expectedId) return null;
            var flags = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
            if ((flags & 0x8000) == 0) return null;                 // not a response
            if (((flags >> 11) & 0xF) != 0) return null;            // not a standard query
            if ((flags & 0x0200) != 0) return null;                 // truncated
            var rcode = flags & 0xF;
            var authoritative = (flags & 0x0400) != 0;
            if (rcode != NoError && rcode != NameError) return new Answer(rcode, authoritative, Array.Empty<Record>());
            var questions = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
            var answers = BinaryPrimitives.ReadUInt16BigEndian(message[6..]);

            var at = 12;
            for (var i = 0; i < questions; i++)
            {
                if (ReadName(message, ref at) == null || at + 4 > message.Length) return null;
                at += 4;
            }

            var records = new List<Record>();
            for (var i = 0; i < answers; i++)
            {
                var owner = ReadName(message, ref at);
                if (owner == null || at + 10 > message.Length) return null;
                var type = BinaryPrimitives.ReadUInt16BigEndian(message[at..]);
                var rdLength = BinaryPrimitives.ReadUInt16BigEndian(message[(at + 8)..]);
                at += 10;
                if (at + rdLength > message.Length) return null;
                var data = message.Slice(at, rdLength);
                string? value = type switch
                {
                    A when rdLength == 4 => new IPAddress(data).ToString(),
                    AAAA when rdLength == 16 => new IPAddress(data).ToString(),
                    CNAME or NS => ReadNameAt(message, at, at + rdLength),
                    MX when rdLength > 2 => ReadNameAt(message, at + 2, at + rdLength),   // preference, then the mail server
                    TXT => ReadText(data),
                    _ => null,
                };
                if (value == null && type is A or AAAA or CNAME or NS or MX or TXT) return null;
                if (value != null) records.Add(new Record(type, owner, value));
                at += rdLength;
            }

            return new Answer(rcode, authoritative, records);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static string Normalize(string? name) => (name ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    // A name inside rdata must end inside it as well.
    private static string? ReadNameAt(ReadOnlySpan<byte> message, int at, int end)
    {
        var name = ReadName(message, ref at);
        return name != null && at <= end ? name : null;
    }

    // A (possibly compressed) name at `at`; `at` moves past it in the record. Null when malformed.
    private static string? ReadName(ReadOnlySpan<byte> message, ref int at)
    {
        var labels = new StringBuilder();
        var position = at;
        var jumps = 0;
        var resumeAt = -1;
        while (true)
        {
            if (position >= message.Length) return null;
            var length = message[position];
            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= message.Length || ++jumps > 16) return null;
                var pointer = ((length & 0x3F) << 8) | message[position + 1];
                if (resumeAt < 0) resumeAt = position + 2;
                if (pointer >= message.Length) return null;
                position = pointer;
                continue;
            }
            if ((length & 0xC0) != 0) return null;                  // reserved label types
            if (length == 0)
            {
                at = resumeAt >= 0 ? resumeAt : position + 1;
                return labels.ToString().ToLowerInvariant();
            }
            if (position + 1 + length > message.Length) return null;
            if (labels.Length > 0) labels.Append('.');
            labels.Append(Encoding.ASCII.GetString(message.Slice(position + 1, length)));
            if (labels.Length > 255) return null;
            position += 1 + length;
        }
    }

    // A TXT record's character-strings, joined into one value (as DomainOwnershipProof joins a resolver's "a" "b").
    private static string? ReadText(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder();
        var at = 0;
        while (at < data.Length)
        {
            var length = data[at];
            if (at + 1 + length > data.Length) return null;
            text.Append(Encoding.UTF8.GetString(data.Slice(at + 1, length)));
            at += 1 + length;
        }
        return text.ToString();
    }
}
