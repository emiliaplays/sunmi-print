using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Serialization;

namespace SunmiPrint
{
    public sealed class ConnectionSettings
    {
        public bool Usb { get; set; }
        public string Address { get; set; }
        public int Port { get; set; }
        public string UsbSerial { get; set; }

        private static readonly string DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SunmiPrint");

        public ConnectionSettings()
        {
            Address = "";
            Port = 5555;
            UsbSerial = "";
        }

        public string DisplayName
        {
            get
            {
                if (Usb)
                    return String.IsNullOrEmpty(UsbSerial) ? "USB" : "USB · " + UsbSerial;
                return String.IsNullOrEmpty(Address) ? "WLAN · IP in den Einstellungen eintragen" : "WLAN · " + Endpoint;
            }
        }

        internal string Endpoint { get { return Address + ":" + Port; } }

        public static ConnectionSettings Load()
        {
            var path = Path.Combine(DirectoryPath, "settings.xml");
            if (File.Exists(path))
            {
                try
                {
                    using (var stream = File.OpenRead(path))
                    {
                        var settings = (ConnectionSettings)new XmlSerializer(typeof(ConnectionSettings)).Deserialize(stream);
                        settings.Validate();
                        return settings;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (InvalidOperationException) { }
            }
            return new ConnectionSettings();
        }

        public void Save()
        {
            Validate();
            Directory.CreateDirectory(DirectoryPath);
            string path = Path.Combine(DirectoryPath, "settings.xml");
            string temporary = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = File.Create(temporary))
                    new XmlSerializer(typeof(ConnectionSettings)).Serialize(stream, this);
                if (File.Exists(path))
                    File.Replace(temporary, path, null);
                else
                    File.Move(temporary, path);
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        internal void Validate()
        {
            if (!Usb)
            {
                IPAddress parsed;
                if (!IPAddress.TryParse(Address, out parsed) || parsed.AddressFamily != AddressFamily.InterNetwork
                    || parsed.ToString() != Address)
                    throw new InvalidOperationException("Trage unter Einstellungen die IPv4-Adresse deines SUNMI ein.");
                if (Port < 1 || Port > 65535)
                    throw new InvalidOperationException("Der WLAN-Port muss zwischen 1 und 65535 liegen.");
            }
            if (!String.IsNullOrEmpty(UsbSerial) && !Regex.IsMatch(UsbSerial, @"\A[A-Za-z0-9_.-]+\z"))
                throw new InvalidOperationException("Die USB-Seriennummer enthält ungültige Zeichen.");
        }
    }

    internal sealed class PrinterStatus
    {
        public readonly bool Ready;
        public readonly string Message;

        public PrinterStatus(bool ready, string message)
        {
            Ready = ready;
            Message = message;
        }
    }

    internal sealed class PrinterConnection
    {
        private const string Package = "de.sunmiprint.bridge";
        private const string Component = Package + "/.PrintBridge";
        private const string RemoteSocket = "localabstract:" + Package + ".v1";
        private const string PrinterPackage = "woyou.aidlservice.jiuiv5";
        private const string BridgeVersion = "1";
        private const string UnknownPrintResult =
            "Die Verbindung wurde beim Drucken unterbrochen. Prüfe den Beleg vor einem erneuten Versuch.";
        private readonly ConnectionSettings settings;
        private readonly string adb;

        public PrinterConnection(ConnectionSettings settings)
        {
            settings.Validate();
            this.settings = settings;
            adb = FindAdb();
        }

        public PrinterStatus Check()
        {
            return WithForward(Query);
        }

        public PrinterStatus Print(ReceiptRaster receipt)
        {
            return WithForward(port =>
            {
                PrinterStatus status = Query(port);
                if (!status.Ready)
                    return status;
                try
                {
                    return Exchange(port, receipt);
                }
                catch (IOException error)
                {
                    throw new InvalidOperationException(UnknownPrintResult, error);
                }
                catch (SocketException error)
                {
                    throw new InvalidOperationException(UnknownPrintResult, error);
                }
            });
        }

        private PrinterStatus WithForward(Func<int, PrinterStatus> action)
        {
            string serial = Connect();
            PrepareBridge(serial);
            string forwarded = Run(12000, "-s", serial, "forward", "tcp:0", RemoteSocket).Trim();
            int localPort;
            if (!Int32.TryParse(forwarded, NumberStyles.None, CultureInfo.InvariantCulture, out localPort)
                || localPort < 1 || localPort > 65535)
                throw new InvalidOperationException("ADB konnte keinen lokalen Druckkanal einrichten. " + forwarded);
            try
            {
                return action(localPort);
            }
            finally
            {
                try
                {
                    Run(3000, "-s", serial, "forward", "--remove", "tcp:" + localPort);
                }
                catch (InvalidOperationException) { }
            }
        }

        private string Connect()
        {
            string endpoint = settings.Usb ? settings.UsbSerial : settings.Endpoint;
            var devices = ReadDevices();
            string detail = "";
            if (!settings.Usb && !devices.Any(device => device[0] == endpoint && device[1] == "device"))
            {
                detail = Run(12000, "connect", endpoint);
                devices = ReadDevices();
            }

            IEnumerable<string[]> candidates = devices.Where(device =>
                String.IsNullOrEmpty(endpoint) ? !device[0].Contains(":") : device[0] == endpoint);
            var choices = candidates.ToArray();
            if (choices.Length > 1 && settings.Usb)
            {
                var sunmiDevices = choices.Where(device => device.Contains("model:V2")).ToArray();
                if (sunmiDevices.Length == 1)
                    choices = sunmiDevices;
            }
            if (choices.Length == 0)
            {
                if (settings.Usb)
                    throw new InvalidOperationException("Kein USB-Gerät gefunden. Prüfe das Datenkabel und aktiviere USB-Debugging.");
                throw new InvalidOperationException("Der SUNMI ist unter " + endpoint + " nicht erreichbar. Prüfe WLAN, IP und ADB-Port."
                    + (String.IsNullOrWhiteSpace(detail) ? "" : "\n" + detail.Trim()));
            }
            if (choices.Length > 1)
                throw new InvalidOperationException("Mehrere USB-Geräte gefunden. Trage in den Einstellungen die USB-Seriennummer ein.");
            if (choices[0][1] == "unauthorized")
                throw new InvalidOperationException("Entsperre den SUNMI und bestätige „USB-Debugging zulassen?“ auf dem Gerät.");
            if (choices[0][1] != "device")
                throw new InvalidOperationException("ADB meldet „" + choices[0][1] + "“. Prüfe die Verbindung und versuche es erneut.");
            return choices[0][0];
        }

        private string[][] ReadDevices()
        {
            return Run(10000, "devices", "-l").Split('\n')
                .Select(line => Regex.Split(line.Trim(), @"\s+"))
                .Where(parts => parts.Length >= 2 && Regex.IsMatch(parts[0], @"\A[A-Za-z0-9_.:\[\]-]+\z")
                    && parts[0] != "List")
                .ToArray();
        }

        private void PrepareBridge(string serial)
        {
            string printer = Run(10000, "-s", serial, "shell", "pm", "path", PrinterPackage);
            if (!printer.Contains("package:"))
                throw new InvalidOperationException("Auf diesem Gerät wurde kein interner SUNMI-Druckdienst gefunden.");
            string installed = Run(10000, "-s", serial, "shell", "dumpsys", "package", Package);
            if (!Regex.IsMatch(installed, @"\bversionCode=" + BridgeVersion + @"\b"))
            {
                string apk = Path.Combine(Path.GetTempPath(), "SunmiPrint-" + Guid.NewGuid().ToString("N") + ".apk");
                try
                {
                    using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream("SunmiBridge.apk"))
                    using (FileStream file = File.Create(apk))
                        source.CopyTo(file);
                    string result = Run(65000, "-s", serial, "install", "-r", apk);
                    if (!Regex.IsMatch(result, @"(?m)^Success\s*$"))
                        throw new InvalidOperationException("Die Druckbrücke konnte nicht installiert werden. " + result.Trim());
                }
                finally
                {
                    File.Delete(apk);
                }
            }
            string sdkText = Run(10000, "-s", serial, "shell", "getprop", "ro.build.version.sdk").Trim();
            int sdk;
            if (!Int32.TryParse(sdkText, out sdk))
                throw new InvalidOperationException("Die Android-Version des SUNMI konnte nicht ermittelt werden.");
            string start = Run(10000, "-s", serial, "shell", "am",
                sdk >= 26 ? "start-foreground-service" : "startservice", "-n", Component);
            if (Regex.IsMatch(start, @"Error|Exception|denied", RegexOptions.IgnoreCase))
                throw new InvalidOperationException("Die Druckbrücke konnte nicht gestartet werden. " + start.Trim());
        }

        private static PrinterStatus Query(int port)
        {
            Exception failure = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    return Exchange(port, null);
                }
                catch (IOException error) { failure = error; }
                catch (SocketException error) { failure = error; }
                if (attempt < 4)
                    Thread.Sleep(250);
            }
            throw new InvalidOperationException("Die Druckbrücke antwortet nicht. Prüfe die ADB-Verbindung zum SUNMI.", failure);
        }

        private static PrinterStatus Exchange(int port, ReceiptRaster receipt)
        {
            using (var client = new TcpClient())
            {
                var connection = client.BeginConnect(IPAddress.Loopback, port, null, null);
                using (var wait = connection.AsyncWaitHandle)
                    if (!wait.WaitOne(2000))
                        throw new IOException("Timeout connecting to the ADB forward");
                client.EndConnect(connection);
                client.ReceiveTimeout = receipt == null ? 7000 : 85000;
                client.SendTimeout = 6000;
                using (NetworkStream stream = client.GetStream())
                {
                    byte[] header = Encoding.ASCII.GetBytes(receipt == null ? "SNP1Q" : "SNP1P");
                    stream.Write(header, 0, header.Length);
                    if (receipt != null)
                    {
                        stream.WriteByte((byte)(receipt.Height >> 8));
                        stream.WriteByte((byte)(receipt.Height & 255));
                        stream.Write(receipt.Packed, 0, receipt.Packed.Length);
                    }
                    using (var reader = new StreamReader(stream, new UTF8Encoding(false, true)))
                    {
                        string reply = reader.ReadLine();
                        int separator = reply == null ? -1 : reply.IndexOf('|');
                        if (separator < 0)
                            throw new IOException("Missing response from the Android print bridge");
                        string state = reply.Substring(0, separator);
                        if (state != "OK" && state != "STATUS" && state != "ERR")
                            throw new IOException("Invalid response from the Android print bridge");
                        return new PrinterStatus(state == "OK", reply.Substring(separator + 1));
                    }
                }
            }
        }

        private string Run(int timeout, params string[] arguments)
        {
            var start = new ProcessStartInfo(adb, String.Join(" ", arguments.Select(Quote)))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            using (Process process = Process.Start(start))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeout))
                {
                    try { process.Kill(); }
                    catch (InvalidOperationException) { }
                    throw new InvalidOperationException("ADB antwortet nicht. Prüfe die Verbindung und versuche es erneut.");
                }
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("ADB meldet einen Fehler. " + (error.Result + output.Result).Trim());
                return output.Result;
            }
        }

        private static string Quote(string value)
        {
            return "\"" + Regex.Replace(value, "(\\\\*)(\"|$)", match =>
                new string('\\', match.Groups[1].Length * 2 + (match.Groups[2].Length == 0 ? 0 : 1))
                + match.Groups[2].Value) + "\"";
        }

        private static string FindAdb()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var directories = new List<string>
            {
                Path.Combine(local, "Android", "platform-tools"),
                Path.Combine(local, "Android", "Sdk", "platform-tools"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "platform-tools")
            };
            foreach (string name in new[] { "ANDROID_SDK_ROOT", "ANDROID_HOME" })
            {
                string sdk = Environment.GetEnvironmentVariable(name);
                if (!String.IsNullOrWhiteSpace(sdk))
                    directories.Add(Path.Combine(sdk, "platform-tools"));
            }
            directories.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
            foreach (string directory in directories)
            {
                if (String.IsNullOrWhiteSpace(directory))
                    continue;
                string candidate = Path.Combine(directory.Trim().Trim('"'), "adb.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            throw new InvalidOperationException("ADB wurde nicht gefunden. Installiere die Android SDK Platform-Tools oder lege den Ordner platform-tools neben die App.");
        }
    }
}
