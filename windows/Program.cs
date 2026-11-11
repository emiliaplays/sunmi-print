using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

[assembly: AssemblyTitle("SUNMI Print")]
[assembly: AssemblyDescription("Windows-Druckapp für den eingebauten Drucker des SUNMI V2")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace SunmiPrint
{
    internal static class Program
    {
        [STAThread]
        public static void Main()
        {
            try
            {
                var application = new Application();
                var ui = new PrintWindow();
                application.Run(ui.Window);
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "SUNMI Print", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        internal static Window LoadWindow(string resource)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
                return (Window)XamlReader.Load(stream);
        }
    }

    internal sealed class PrintWindow
    {
        private static readonly SolidColorBrush Neutral = Brush(0x84, 0x96, 0x90);
        private static readonly SolidColorBrush Ready = Brush(0x17, 0x80, 0x60);
        private static readonly SolidColorBrush Working = Brush(0xBA, 0x7A, 0x20);
        private static readonly SolidColorBrush Error = Brush(0xA8, 0x3F, 0x33);

        public readonly Window Window = Program.LoadWindow("MainWindow.xaml");
        private readonly DispatcherTimer previewTimer;
        private readonly TextBlock targetLabel;
        private readonly TextBlock statusLabel;
        private readonly Ellipse statusDot;
        private readonly TextBox editor;
        private readonly TextBlock editorHint;
        private readonly RadioButton textMode;
        private readonly RadioButton imageMode;
        private readonly Border imagePanel;
        private readonly TextBlock imageName;
        private readonly Image preview;
        private readonly Border paper;
        private readonly TextBlock previewHint;
        private readonly Button connect;
        private readonly Button settingsButton;
        private readonly Button open;
        private readonly Button test;
        private readonly Button print;
        private ConnectionSettings settings = ConnectionSettings.Load();
        private ReceiptRaster importedImage;
        private ReceiptRaster receipt;
        private bool busy;

        public PrintWindow()
        {
            targetLabel = Find<TextBlock>("TargetLabel");
            statusLabel = Find<TextBlock>("StatusLabel");
            statusDot = Find<Ellipse>("StatusDot");
            editor = Find<TextBox>("Editor");
            editorHint = Find<TextBlock>("EditorHint");
            textMode = Find<RadioButton>("TextMode");
            imageMode = Find<RadioButton>("ImageMode");
            imagePanel = Find<Border>("ImagePanel");
            imageName = Find<TextBlock>("ImageName");
            preview = Find<Image>("Preview");
            paper = Find<Border>("Paper");
            previewHint = Find<TextBlock>("PreviewHint");
            connect = Find<Button>("ConnectButton");
            settingsButton = Find<Button>("SettingsButton");
            open = Find<Button>("OpenButton");
            test = Find<Button>("TestButton");
            print = Find<Button>("PrintButton");
            ConfigureTemplates(Find<Button>("TemplatesButton"));

            previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            previewTimer.Tick += (sender, args) => UpdatePreview();
            editor.TextChanged += (sender, args) =>
            {
                editorHint.Visibility = editor.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                previewTimer.Stop();
                previewTimer.Start();
            };
            textMode.Checked += (sender, args) => UpdateMode();
            imageMode.Checked += (sender, args) => UpdateMode();
            open.Click += (sender, args) => OpenFile();
            settingsButton.Click += (sender, args) => OpenSettings();
            connect.Click += async (sender, args) => await Send(null, "Verbinde mit dem SUNMI …");
            test.Click += async (sender, args) => await Send(ReceiptRaster.FromText(
                "SUNMI V2\nTestdruck von Windows\n------------------------------\n" +
                DateTime.Now.ToString("dd.MM.yyyy  HH:mm", CultureInfo.GetCultureInfo("de-DE"))), "Drucke Testbeleg …");
            print.Click += async (sender, args) =>
            {
                UpdatePreview();
                if (receipt != null)
                    await Send(receipt, "Drucke Beleg …");
            };
            Window.Closing += (sender, args) =>
            {
                if (busy)
                    args.Cancel = true;
            };
            Window.Loaded += (sender, args) => editor.Focus();
            targetLabel.Text = settings.DisplayName;
        }

        private T Find<T>(string name) where T : FrameworkElement
        {
            return (T)Window.FindName(name);
        }

        private void ConfigureTemplates(Button button)
        {
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom, MinWidth = 160 };
            AddTemplate(menu, "Verkaufsbeleg",
                "VERKAUFSBELEG",
                "[Geschäft]",
                "Datum: [TT.MM.JJJJ]",
                "",
                "Artikel                  Preis",
                "[Artikel]            [0,00 EUR]",
                "------------------------------",
                "Gesamt:              [0,00 EUR]",
                "Zahlung: [Bar / Karte]",
                "",
                "Danke für deinen Einkauf!");
            AddTemplate(menu, "Küchenbon",
                "KÜCHENBON",
                "Bestellung: [Nr.]",
                "Tisch: [Nr.]    Zeit: [HH:MM]",
                "------------------------------",
                "[Menge] x [Gericht]",
                "[Menge] x [Gericht]",
                "",
                "Hinweise: [Sonderwünsche]");
            AddTemplate(menu, "Abholschein",
                "ABHOLSCHEIN",
                "Auftrag: [Nr.]",
                "Name: [Kundenname]",
                "Abholung: [Datum und Uhrzeit]",
                "",
                "Inhalt: [Beschreibung]");
            AddTemplate(menu, "Preisetikett",
                "[PRODUKTNAME]",
                "[Variante oder Größe]",
                "",
                "PREIS: [0,00 EUR]",
                "Artikel-Nr.: [SKU]");
            AddTemplate(menu, "Inventaretikett",
                "INVENTAR",
                "[Bezeichnung]",
                "",
                "Inventar-Nr.: [Nummer]",
                "Standort: [Raum oder Regal]",
                "Erfasst am: [TT.MM.JJJJ]");
            button.ContextMenu = menu;
            button.Click += (sender, args) => menu.IsOpen = true;
        }

        private void AddTemplate(ContextMenu menu, string title, params string[] lines)
        {
            var item = new MenuItem { Header = title };
            item.Click += (sender, args) =>
            {
                textMode.IsChecked = true;
                editor.BeginChange();
                try
                {
                    editor.SelectAll();
                    editor.SelectedText = String.Join(Environment.NewLine, lines);
                }
                finally
                {
                    editor.EndChange();
                }
                editor.Focus();
                int start = editor.Text.IndexOf('[');
                int end = editor.Text.IndexOf(']', start + 1);
                if (start >= 0 && end > start)
                    editor.Select(start, end - start + 1);
                UpdatePreview();
            };
            menu.Items.Add(item);
        }

        private void UpdateMode()
        {
            bool text = textMode.IsChecked == true;
            editor.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
            editorHint.Visibility = text && editor.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            imagePanel.Visibility = text ? Visibility.Collapsed : Visibility.Visible;
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            previewTimer.Stop();
            string message = "Dein Beleg erscheint hier";
            receipt = null;
            try
            {
                if (textMode.IsChecked != true)
                    receipt = importedImage;
                else if (!String.IsNullOrWhiteSpace(editor.Text))
                    receipt = ReceiptRaster.FromText(editor.Text);
            }
            catch (InvalidOperationException error)
            {
                message = error.Message;
            }
            preview.Source = receipt == null ? null : receipt.Preview;
            paper.MinHeight = receipt == null ? 122 : 0;
            previewHint.Text = message;
            previewHint.Visibility = receipt == null ? Visibility.Visible : Visibility.Collapsed;
            print.IsEnabled = !busy && receipt != null;
        }

        private void OpenFile()
        {
            bool text = textMode.IsChecked == true;
            var dialog = new OpenFileDialog
            {
                Title = text ? "Textdatei öffnen" : "Bilddatei öffnen",
                Filter = text ? "Textdateien (*.txt)|*.txt" : "Bilder (*.png, *.jpg, *.jpeg, *.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(Window) != true)
                return;
            try
            {
                if (text)
                {
                    string content;
                    try { content = ReadText(dialog.FileName, new UTF8Encoding(false, true)); }
                    catch (DecoderFallbackException) { content = ReadText(dialog.FileName, Encoding.GetEncoding(1252)); }
                    editor.Text = content;
                    editor.Focus();
                }
                else
                {
                    importedImage = ReceiptRaster.FromImage(dialog.FileName);
                    imageName.Text = System.IO.Path.GetFileName(dialog.FileName);
                }
                UpdatePreview();
            }
            catch (Exception error)
            {
                MessageBox.Show(Window, error.Message, "Datei konnte nicht geöffnet werden",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private string ReadText(string path, Encoding encoding)
        {
            var chars = new char[editor.MaxLength + 1];
            using (var reader = new StreamReader(path, encoding, true))
            {
                int count = reader.ReadBlock(chars, 0, chars.Length);
                if (count == chars.Length)
                    throw new InvalidOperationException("Die Textdatei darf höchstens " + editor.MaxLength + " Zeichen enthalten.");
                return new string(chars, 0, count);
            }
        }

        private void OpenSettings()
        {
            ConnectionSettings next = SettingsDialog.Edit(Window, settings);
            if (next != null)
            {
                if (settings.DisplayName != next.DisplayName)
                    ShowStatus("Nicht verbunden", Neutral);
                settings = next;
                targetLabel.Text = settings.DisplayName;
            }
        }

        private async Task Send(ReceiptRaster job, string progress)
        {
            SetBusy(true);
            ShowStatus(progress, Working);
            var target = settings;
            try
            {
                PrinterStatus status = await Task.Run(() =>
                {
                    var connection = new PrinterConnection(target);
                    return job == null ? connection.Check() : connection.Print(job);
                });
                ShowStatus(status.Message, status.Ready ? Ready : Error);
            }
            catch (Exception error)
            {
                ShowStatus(error.Message, Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SetBusy(bool value)
        {
            busy = value;
            connect.IsEnabled = settingsButton.IsEnabled = test.IsEnabled = !value;
            print.IsEnabled = !value && receipt != null;
        }

        private void ShowStatus(string message, SolidColorBrush brush)
        {
            statusDot.Fill = brush;
            statusLabel.Text = message;
        }

        private static SolidColorBrush Brush(byte red, byte green, byte blue)
        {
            return new SolidColorBrush(Color.FromRgb(red, green, blue));
        }
    }

    internal static class SettingsDialog
    {
        public static ConnectionSettings Edit(Window owner, ConnectionSettings current)
        {
            var dialog = Program.LoadWindow("SettingsWindow.xaml");
            dialog.Owner = owner;
            var wifi = (RadioButton)dialog.FindName("Wifi");
            var usb = (RadioButton)dialog.FindName("Usb");
            var networkFields = (StackPanel)dialog.FindName("NetworkFields");
            var usbFields = (StackPanel)dialog.FindName("UsbFields");
            var address = (TextBox)dialog.FindName("Address");
            var port = (TextBox)dialog.FindName("Port");
            var serial = (TextBox)dialog.FindName("Serial");
            var validation = (TextBlock)dialog.FindName("Validation");
            var save = (Button)dialog.FindName("SaveButton");
            ConnectionSettings saved = null;
            RoutedEventHandler modeChanged = (sender, args) =>
            {
                networkFields.Visibility = usb.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
                usbFields.Visibility = usb.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                validation.Visibility = Visibility.Collapsed;
            };
            wifi.Checked += modeChanged;
            usb.Checked += modeChanged;
            (current.Usb ? usb : wifi).IsChecked = true;
            address.Text = current.Address;
            port.Text = current.Port.ToString(CultureInfo.InvariantCulture);
            serial.Text = current.UsbSerial;
            save.Click += (sender, args) =>
            {
                bool useUsb = usb.IsChecked == true;
                int number = current.Port;
                try
                {
                    if (!useUsb && !Int32.TryParse(port.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out number))
                        throw new InvalidOperationException("Gib einen ADB-Port zwischen 1 und 65535 ein.");
                    var next = new ConnectionSettings
                    {
                        Usb = useUsb,
                        Address = useUsb ? current.Address : address.Text.Trim(),
                        Port = number,
                        UsbSerial = useUsb ? serial.Text.Trim() : current.UsbSerial
                    };
                    next.Save();
                    saved = next;
                    dialog.DialogResult = true;
                }
                catch (Exception error)
                {
                    validation.Text = error.Message;
                    validation.Visibility = Visibility.Visible;
                }
            };
            dialog.ShowDialog();
            return saved;
        }
    }
}
