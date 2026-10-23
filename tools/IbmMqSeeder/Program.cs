using System.Text;
using DynaQueueExplorer.Core.Clusters;
using DynaQueueExplorer.IbmMq.Client;
using DynaQueueExplorer.IbmMq.Formats;
using IBM.WMQ;

// Fills the local IBM MQ queue manager (docker-compose service "ibmmq") with messages in the formats met in practice:
// MQSTR (UTF-8 JSON/XML, EBCDIC records), JMS messages with RFH2, binary, dead-letter messages (MQDLH), messages on a
// transmission queue (real MQXQH written by the queue manager) and a PCF message.
//
//   dotnet run --project tools/IbmMqSeeder [-- --certs docker/rabbitmq/certs]

var certs = args.SkipWhile(a => a != "--certs").Skip(1).FirstOrDefault() ?? "docker/rabbitmq/certs";
var options = new ClusterOptions
{
    Name = "local-mq",
    Broker = "ibmmq",
    QueueManager = "QM1",
    Host = "localhost",
    Port = 1414,
    Channel = "DEV.APP.SVRCONN",
    CipherSpec = "ECDHE_RSA_AES_256_GCM_SHA384",
    ClientCertificatePath = Path.GetFullPath(Path.Combine(certs, "client.p12")),
    ClientCertificatePassword = "dqe-local",
    Username = "app",
    Password = "Dqe_Mq_Dev_2026!",
};

const int Little = 546;      // MQENC_NATIVE on x86/ARM Linux and Windows
const int ZosEncoding = 785; // big-endian integers, z/OS
var random = new Random(2026);
var now = DateTime.UtcNow;
var utf8 = Encoding.UTF8;
var ebcdic = Ccsids.GetEncoding(1144)!; // Italian EBCDIC with euro

using var qm = new MQQueueManager(options.QueueManager, MqConnectionProperties.For(options));
var counts = new Dictionary<string, int>();

void Put(string queue, byte[] data, string format, int ccsid, int encoding, Action<MQMessage>? configure = null)
{
    var q = qm.AccessQueue(queue, MQC.MQOO_OUTPUT | MQC.MQOO_FAIL_IF_QUIESCING);
    var m = new MQMessage { Format = format, CharacterSet = ccsid, Encoding = encoding, Persistence = MQC.MQPER_PERSISTENT };
    configure?.Invoke(m);
    m.Write(data);
    q.Put(m, new MQPutMessageOptions());
    q.Close();
    counts[queue] = counts.GetValueOrDefault(queue) + 1;
}

string Order(int i) => $$"""{"orderId":"ORD-{{100000 + i}}","customer":{{random.Next(1000, 9999)}},"amount":{{Math.Round(random.NextDouble() * 900 + 10, 2).ToString(System.Globalization.CultureInfo.InvariantCulture)}},"currency":"EUR","lines":[{"sku":"SKU-{{random.Next(10000, 99999)}}","qty":{{random.Next(1, 9)}}}]}""";

// 1. Plain strings, UTF-8
for (var i = 0; i < 25; i++)
    Put("ORDERS.IN", utf8.GetBytes(Order(i)), MqFormat.String, 1208, Little, m => m.CorrelationId = utf8.GetBytes($"BATCH-{i / 10:D2}".PadRight(24)));
for (var i = 0; i < 5; i++)
    Put("ORDERS.IN", utf8.GetBytes($"<order id=\"ORD-{200000 + i}\"><customer>{random.Next(1000, 9999)}</customer><amount currency=\"EUR\">{random.Next(10, 999)}.50</amount></order>"),
        MqFormat.String, 1208, Little);

// 2. JMS messages: RFH2 with mcd/jms/usr folders
string Jms(int i, string domain) => $"<jms><Dst>queue:///ORDERS.JMS</Dst><Tms>{DateTimeOffset.UtcNow.AddMinutes(-i).ToUnixTimeMilliseconds()}</Tms><Cid>CORR-{i}</Cid><Dlv>2</Dlv></jms>";
for (var i = 0; i < 20; i++)
{
    var rfh2 = new MqWriter().Rfh2(MqFormat.String, 1208, Little,
        "<mcd><Msd>jms_text</Msd></mcd>", Jms(i, "text"),
        $"<usr><OrderType>{(i % 3 == 0 ? "express" : "standard")}</OrderType><Channel>web</Channel><Retries dt='i4'>{i % 4}</Retries></usr>")
        .Bytes(utf8.GetBytes(Order(1000 + i))).ToArray();
    Put("ORDERS.JMS", rfh2, MqFormat.Rfh2, 1208, Little);
}
for (var i = 0; i < 5; i++)
{
    var payload = new byte[64];
    random.NextBytes(payload);
    var rfh2 = new MqWriter().Rfh2(MqFormat.None, 1208, Little, "<mcd><Msd>jms_bytes</Msd></mcd>", Jms(i, "bytes"),
        "<usr><ContentKind>pdf-fragment</ContentKind></usr>").Bytes(payload).ToArray();
    Put("ORDERS.JMS", rfh2, MqFormat.Rfh2, 1208, Little);
}

// 3. Mainframe: fixed-length EBCDIC records, big-endian encoding
var cities = new[] { "Città di Castello", "Forlì", "Cantù", "Mondovì", "Lecco" };
for (var i = 0; i < 15; i++)
{
    var record = $"ORD{300000 + i:D7}CLI{random.Next(10000, 99999):D6}{cities[i % cities.Length],-20}EUR{random.Next(100, 99999):D9}€ STATO:SPEDITO  ";
    Put("MAINFRAME.EBCDIC", ebcdic.GetBytes(record), MqFormat.String, 1144, ZosEncoding, m => m.PutApplicationName = "CICSPAY1");
}

// 4. Binary payloads without a format
for (var i = 0; i < 10; i++)
{
    var payload = new byte[random.Next(48, 300)];
    random.NextBytes(payload);
    Put("BINARY.FEED", payload, MqFormat.None, 1208, Little);
}

// 5. Dead-letter queue: DLH in front of JSON, JMS (RFH2) or EBCDIC data
var reasons = new[] { (2053, "ORDERS.FULL"), (2085, "ORDERS.TYPO"), (2035, "PAYMENTS.SECURE"), (258, "ORDERS.IN") };
for (var i = 0; i < 12; i++)
{
    var (reason, dest) = reasons[i % reasons.Length];
    var withJms = i % 2 == 0;
    var w = new MqWriter().DeadLetterHeader(reason, dest, "QM1", withJms ? MqFormat.Rfh2 : MqFormat.String, 1208, Little, "amqrmppa", now.AddMinutes(-i * 7));
    if (withJms)
        w.Rfh2(MqFormat.String, 1208, Little, "<mcd><Msd>jms_text</Msd></mcd>", Jms(i, "text"), "<usr><OrderType>express</OrderType></usr>");
    w.Bytes(utf8.GetBytes(Order(2000 + i)));
    Put("DEV.DEAD.LETTER.QUEUE", w.ToArray(), MqFormat.DeadLetter, 1208, Little);
}
var mvs = new MqWriter(bigEndian: true, Ccsids.GetEncoding(500)).DeadLetterHeader(2087, "PAY.SETTLEMENT", "MVSQM9", MqFormat.String, 1144, ZosEncoding, "CSQXRCTL")
    .Bytes(ebcdic.GetBytes("SETTLEMENT 0000123 IMPORTO 1.250,00 €")).ToArray();
Put("DEV.DEAD.LETTER.QUEUE", mvs, MqFormat.DeadLetter, 500, ZosEncoding);

// 6. Remote queue without a channel: the queue manager stores them on QM2.XMIT with a real MQXQH
for (var i = 0; i < 5; i++)
    Put("PARTNER.ORDERS", utf8.GetBytes(Order(3000 + i)), MqFormat.String, 1208, Little);

// 7. A PCF message (a response to INQUIRE_Q with name and depth), as found on reply or event queues
var pcf = new MqWriter()
    .Int32(2).Int32(36).Int32(1).Int32(MQC.MQCMD_INQUIRE_Q).Int32(1).Int32(1).Int32(0).Int32(0).Int32(2)
    .Int32(4).Int32(20 + 48).Int32(MQC.MQCA_Q_NAME).Int32(1208).Int32(48).String("ORDERS.IN", 48)
    .Int32(3).Int32(16).Int32(MQC.MQIA_CURRENT_Q_DEPTH).Int32(30).ToArray();
Put("DEV.QUEUE.2", pcf, MqFormat.Admin, 1208, Little);

foreach (var (queue, n) in counts.OrderBy(c => c.Key))
    Console.WriteLine($"{queue,-24} +{n}");
