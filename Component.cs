using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using LiveSplit.Model;
using LiveSplit.Options;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using USB2SnesW;
using System.Drawing;
using System.Collections;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("LiveSplit.USB2SNESSplitterTests")]

namespace LiveSplit.UI.Components
{
    public static class MyExtensions
    {
        public static uint ToWramAddress(this uint i)
        {
            if (i > 0xffff)
            {
                throw new Exception($"Invalid WRAM address: {i} > 0xffff");
            }

            return USB2SNESComponent.SD2SNES_WRAM_BASE_ADDRESS + (i & 0xffff);
        }
        public static uint ToFormattedUInt32(this String numberString)
        {
            return Convert.ToUInt32(numberString, numberString.StartsWith("0x") ? 16 : 10);
        }
    }

    public class USB2SNESComponent : IComponent
    {
        public const uint SD2SNES_WRAM_BASE_ADDRESS = 0xF50000;
        public const uint DEFAULT_READ_SIZE = 64;

        enum MyState
        {
            NONE,
            ERROR,
            CONNECTING,
            READY,
        };
        enum ProtocolState // Only when attached we are good
        {
            NONE,
            CONNECTED,
            ATTACHED
        }

        internal class Split
        {
            public string Active { get; set; }
            public string Name { get; set; }
            public string Address { get; set; }
            public string Value { get; set; }
            public string Type { get; set; }
            public string Operator { get; set; }
            public int Repeat { get; set; } = 0;
            public List<Split> More { get; set; }
            public List<Split> Next { get; set; }
            public int NextIndex { get; set; } = 0;

            public uint AddressInt { get { return Address.ToFormattedUInt32(); } }
            public uint ValueInt { get { return Value.ToFormattedUInt32(); } }

            public uint? PreviousValueInt { get; set; }

            public bool Check(byte[] data, bool debug)
            {
                var types = new Dictionary<string, Func<byte[], uint>>()
                {
                    { "byte", array => (uint)array[0] },
                    { "short", array => (uint)(array[0] + (array[1] << 8)) },
                };
                var operators = new Dictionary<string, Func<uint, Split, int?, bool>>()
                {
                    { "&", (v, s, d) => (v & s.ValueInt) == v },
                    { "==", (v, s, d) => v == s.ValueInt },
                    { ">", (v, s, d) => v > s.ValueInt },
                    { "<", (v, s, d) => v < s.ValueInt },
                    { ">=", (v, s, d) => v >= s.ValueInt },
                    { "<=", (v, s, d) => v <= s.ValueInt },
                    { "delta", (v, s, d) => d.HasValue && d >= s.ValueInt },
                    { "odelta", (v, s, d) => d.HasValue && (((s.PreviousValueInt + s.ValueInt) & 0xffff) == v) },
                };

                if (this.Type == null || !types.TryGetValue(this.Type, out Func<byte[], uint> type))
                {
                    type = types["short"];
                }
                uint value = type(data);

                int? delta = null;
                if (this.PreviousValueInt.HasValue)
                {
                    delta = (int)value - (int)this.PreviousValueInt;
                }

                bool result = operators[this.Operator](value, this, delta);

                if (debug)
                {
                    Debug.WriteLine($"split[{this.Name}][{this.Next?.Count()}] {this.Address } = {value}/{this.ValueInt} == {result} (delta={delta}, prev={this.PreviousValueInt})");
                }

                this.PreviousValueInt = value;

                return result;
            }
        }

        class Category
        {
            public string Name { get; set; }
            public List<string> Splits { get; set; }
        }

        class Game
        {
            public string Name { get; set; }
            public string Autostart { get; set; }
            public List<Category> Categories { get; set; }
            public List<Split> Definitions { get; set; }
        }

        public string ComponentName => "USB2SNES Auto Splitter";

        public float HorizontalWidth { get; set; }

        public float MinimumHeight => 3;

        public float VerticalHeight { get; set; }

        public float MinimumWidth => 3;

        public float PaddingTop => 1;

        public float PaddingBottom => 1;

        public float PaddingLeft => 1;

        public float PaddingRight => 1;

        public IDictionary<string, Action> ContextMenuControls => null;

        private USB2SnesW.USB2SnesW _usb2snes;
        private MyState _mystate;
        private ProtocolState _proto_state;
        private bool _stateChanged;

        private LiveSplitState _state;
        private ComponentSettings _settings;
        private TimerModel _model;
        private Timer _update_timer;
        private bool _do_reload = true;
        private string _old_script_path;

        private Game _game;
        private Split _autostart;
        private List<Split> _splits;
        private bool _inTimer;
        private string _old_category;

        private Color _ok_color = Color.FromArgb(0, 128, 0);
        private Color _error_color = Color.FromArgb(128, 0, 0);
        private Color _connecting_color = Color.FromArgb(128, 128, 0);

        private void Init(LiveSplitState state, USB2SnesW.USB2SnesW usb2snesw)
        {
            _usb2snes = usb2snesw;
            _mystate = MyState.NONE;
            _proto_state = ProtocolState.NONE;
            _stateChanged = false;

            _state = state;
            _settings = new ComponentSettings();
            _model = new TimerModel() { CurrentState = _state };
            _state.RegisterTimerModel(_model);
            _state.OnReset += _state_OnReset;
            _state.OnStart += _state_OnStart;

            _update_timer = new Timer() { Interval = 1000 };
            _update_timer.Tick += (sender, args) => UpdateSplitsWrapper();
            _update_timer.Enabled = true;
            _splits = new List<Split>();
            _inTimer = false;

            HorizontalWidth = 3;
            VerticalHeight = 3;
        }

        public USB2SNESComponent(LiveSplitState state)
        {
            Init(state, new USB2SnesW.USB2SnesW());
        }

        internal USB2SNESComponent(LiveSplitState state, USB2SnesW.USB2SnesW usb2snesw)
        {
            Init(state, usb2snesw);

        }

        private void ShowMessage(String msg)
        {
            MessageBox.Show(msg, "USB2Snes AutoSplitter");
        }
        private void SetState(MyState state)
        {
            if (_mystate == state)
            {
                return;
            }
            Debug.WriteLine($"USB2SNES state = {state}");
            _stateChanged = true;
            _mystate = state;
        }

        private async void WsAttach(ProtocolState prevState)
        {
            List<String> devices;
            try
            {
                devices = await _usb2snes.GetDevices();
            }
            catch (Exception e)
            {
                Console.WriteLine("Exception getting devices: " + e);
                devices = new List<String>();
            }

            if (!devices.Contains(_settings.Device))
            {
                if (prevState == ProtocolState.NONE)
                    Debug.WriteLine($"Could not find the device '{_settings.Device}'. Check your configuration or activate your device.");
                return;
            }
            _usb2snes.Attach(_settings.Device);
            var info = await _usb2snes.Info(); // Info is the only neutral way to know if we are attached to the device
            if (info.version == "")
            {
                SetState(MyState.ERROR);
            } else {
                SetState(MyState.READY);
                _proto_state = ProtocolState.ATTACHED;
            }
        }

        private void Connect()
        {
            ProtocolState prevState = _proto_state;
            var connected = _usb2snes.Connected();
            if (_proto_state != ProtocolState.CONNECTED || !connected)
            {
                SetState(MyState.CONNECTING);
                Task<bool> t = _usb2snes.Connect();
                t.ContinueWith((t1) =>
                {
                    if (!t1.Result)
                    {
                        SetState(MyState.NONE);
                        _proto_state = ProtocolState.NONE;
                        return;
                    }
                    _usb2snes.SetName("LiveSplit AutoSplitter");
                    _proto_state = ProtocolState.CONNECTED;
                    WsAttach(prevState);
                });
            } else {
                if (connected)
                    WsAttach(prevState);
            }
        }

        private bool CheckSplitsSetting()
        {
            bool r = true;
            foreach (var c in _game.Categories)
            {
                foreach (var s in c.Splits)
                {
                    var d = _game.Definitions.Where(x => x.Name == s).FirstOrDefault();
                    if (d == null)
                    {
                        ShowMessage(String.Format($"Split definition missing: {s} for category {c.Name}"));
                        r = false;
                    }
                }
            }

            return r;
        }

        private bool CheckRunnableSetting()
        {
            if(_game == null)
            {
                return false;
            }

            List<String> splits = new List<string>(_game.Categories.Where(c => c.Name.ToLower() == _state.Run.CategoryName.ToLower()).First()?.Splits);

            if (splits.Count == 0)
            {
                Debug.WriteLine("There are no splits for the current category in the split config file, check that the run category is correctly set and exists in the config file.");
                return false;
            }
            if (_state.Run.Count() != splits.Count())
            {
                Debug.WriteLine(String.Format($"The segment count <{_splits.Count()}> does not match the Autosplitter setting file <{_state.Run.Count()}>"));
            }

            return true;
        }

        // Let's build the split list based on the user segment list and not the category definition
        private void SetSplitList()
        {
            var catSplits = _game.Categories.Where(c => c.Name.ToLower() == _state.Run.CategoryName.ToLower()).First().Splits;
            var splits = catSplits.Select(Name => _game.Definitions.Where(s => s.Name.ToLower() == Name.ToLower()).First()).ToList();

            _splits.Clear();
            foreach (Split split in splits)
            {
                _splits.AddRange(Enumerable.Repeat(split, split.Repeat + 1).ToList());
            }
        }
        private void SetAutostart()
        {
            _autostart = _game.Definitions.Where(s => s.Name == _game.Autostart).FirstOrDefault();
        }

        private void _state_OnStart(object sender, EventArgs e)
        {
            Console.WriteLine("On START?");
            return;
        }

        private void _state_OnReset(object sender, TimerPhase value)
        {
            if (_usb2snes.Connected())
            {
                if (_settings.ResetSNES)
                {
                    _usb2snes.Reset();
                }
            }
        }

        public void Dispose()
        {
            _update_timer?.Dispose();
            if (_usb2snes.Connected())
            {
                _usb2snes.Disconnect();
            }
            _state.OnStart -= _state_OnStart;
            _state.OnReset -= _state_OnReset;
        }

        public Control GetSettingsControl(LayoutMode mode)
        {
            return _settings;
        }

        public XmlNode GetSettings(XmlDocument document)
        {
            return _settings.GetSettings(document);
        }

        public void SetSettings(XmlNode settings)
        {
            _settings.SetSettings(settings);
        }

        public void Update(IInvalidator invalidator, LiveSplitState state, float width, float height,
            LayoutMode mode)
        {
            if (invalidator != null && _stateChanged)
            {
                _stateChanged = false;
                invalidator.Invalidate(0, 0, width, height);
            }
        }

        private bool IsConnectionReady()
        {
            // Debug.WriteLine("Checking connection");
            if (_proto_state == ProtocolState.ATTACHED)
            {
                return true;
            }

            Debug.WriteLine("Connection failed, trying again...");

            // this method actually does a BLOCKING request-response cycle (!!)
            if (!_usb2snes.Connected())
            {
                SetState(MyState.NONE);
                _proto_state = ProtocolState.NONE;
            }

            Connect();
            return false;
        }

        private async void UpdateSplitsWrapper()
        {

            // Debug.WriteLine("Timer tick " + DateTime.Now);
            // "_inTimer" is a very questionable attempt at locking, but it's probably fine here.
            if (_inTimer)
            {
                // Debug.WriteLine("In timer already! !!!");
                return;
            }
            _inTimer = true;
            try
            {
                await UpdateSplits();
            }  catch (Exception e)
            {
                Debug.WriteLine($"Something bad happened: {e}");
                Connect();
            } finally {
                _inTimer = false;
            }
        }

        public async Task UpdateSplits()
        {
            if (!IsConnectionReady() || !IsConfigReady())
            {
                _update_timer.Interval = 1000;
                return;
            }
            else
            {
                _update_timer.Interval = 33;
            }
                        
            await CheckSplits();
        }

        private bool IsConfigReady()
        {
            if (UpdateConfigFile())
            { 
                try
                {
                    SetSplitList();
                    SetAutostart();
                    _do_reload = false;

                    CheckRunnableSetting();

                    Debug.WriteLine($"{_splits.Count} splits detected:");
                    foreach (var split in _splits)
                    {
                        Debug.WriteLine($"- {split.Name}");
                    }
                } catch (Exception e)
                {
                    Debug.WriteLine($"Splits could not be parsed: {e}");
                    return false;
                }
            }

            return !_do_reload;
        }

        private bool UpdateConfigFile()
        {
            if (_old_script_path == null || _settings.ConfigFile != _old_script_path)
            {
                _old_script_path = _settings.ConfigFile;
                _do_reload = true;
            }

            try
            {
                var jsonString = File.ReadAllText(_settings.ConfigFile);
                _game = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Game>(jsonString);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"Could not open split config file, check config file settings: {e.Message}");
                return false;
            }

            if (!CheckSplitsSetting())
            {
                ShowMessage("The split config file has missing definitions.");
                return false;
            }

            return _do_reload;
        }

        private async Task CheckSplits()
        {
            Split split;
            if (_settings.Autostart && _state.CurrentPhase == TimerPhase.NotRunning && _autostart != null)
            {
                split = _autostart;
            }
            else if (_state.CurrentPhase == TimerPhase.Running)
            {
                split = _splits[_state.CurrentSplitIndex];
            } else
            {
                return;
            }

            var orignSplit = split;
            if (split.Next != null && split.NextIndex != 0)
            {
                split = split.Next[split.NextIndex - 1];
            }
            bool ok = await CheckSplit(split);
            if (orignSplit.Next != null && ok)
            {
                Debug.WriteLine($"Next count :{orignSplit.Next.Count} - Pos to check: {orignSplit.NextIndex}");
                if (orignSplit.NextIndex < orignSplit.Next.Count())
                {
                    orignSplit.NextIndex++;
                    ok = false;
                }
                else
                {
                    orignSplit.NextIndex = 0;
                }
            }
            if (split.More != null)
            {
                foreach (var moreSplit in split.More)
                {
                    if (!ok)
                    {
                        break;
                    }
                    ok = ok && await CheckSplit(moreSplit);
                }
            }

            if (ok)
            {
                if(orignSplit == _autostart)
                {
                    _model.Start();
                } else {
                    _model.Split();
                }
            }
        }

        async Task<bool> CheckSplit(Split split)
        {
            byte[] data;
            try
            {
                data = await _usb2snes.GetAddress(split.AddressInt.ToWramAddress(), DEFAULT_READ_SIZE);
            }
            catch
            {
                return false;
            }
            if (data.Count() == 0)
            {
                Console.WriteLine("Get address failed to return result");
                return false;
            }
            return split.Check(data, _settings.Debug);
        }

        public void DrawHorizontal(Graphics graphics, LiveSplitState state, float height, Region clipRegion)
        {
            VerticalHeight = height;
            HorizontalWidth = 3;
        }

        public void DrawVertical(Graphics graphics, LiveSplitState state, float width, Region clipRegion)
        {
            VerticalHeight = 3 + PaddingTop + PaddingBottom;
            HorizontalWidth = width;
            Color color;
            switch (_mystate)
            {
                case MyState.READY: color = _ok_color; break;
                case MyState.CONNECTING: color = _connecting_color; break;
                default: color = _error_color; break;
            }
            Brush brush = new SolidBrush(color);
            graphics.FillRectangle(brush, 0, 0, width, 3);
        }
    }
}
