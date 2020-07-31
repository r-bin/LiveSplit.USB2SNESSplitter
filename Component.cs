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
            public string Name { get; set; }
            public string Address { get; set; }
            public string Value { get; set; }
            public string Type { get; set; }
            public string Active { get; set; }
            public List<Split> More { get; set; }
            public List<Split> Next { get; set; }
            public int PosToCheck { get; set; } = 0;

            public uint AddressInt { get { return Address.ToFormattedUInt32(); } }
            public uint ValueInt { get { return Value.ToFormattedUInt32(); } }

            public uint? PreviousValueInt { get; set; }

            public bool Check(byte[] data)
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

                if (!types.TryGetValue(this.Type, out Func<byte[], uint> type))
                {
                    type = types["short"];
                }
                uint value = type(data);

                int? delta = null;
                if (this.PreviousValueInt.HasValue)
                {
                    delta = (int)value - (int)this.PreviousValueInt;
                }

                bool result = operators[this.Type](value, this, delta);

                // Console.WriteLine($"split[{this.Name}][{_state.CurrentSplitIndex}/{this.Next?.Count()}] {this.Address } = {value}/{this.ValueInt} == {result} (delta={delta}, prev={this.PreviousValueInt})");

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

        private Timer _update_timer;
        private ComponentSettings _settings;
        private LiveSplitState _state;
        private TimerModel _model;
        private Game _game;
        private Split _autostart;
        private List<Split> _splits;
        private MyState _mystate;
        private ProtocolState _proto_state;
        private bool _inTimer;
        private bool _valid_config;
        private bool _config_checked;
        private USB2SnesW.USB2SnesW _usb2snes;
        private Color _ok_color = Color.FromArgb(0, 128, 0);
        private Color _error_color = Color.FromArgb(128, 0, 0);
        private Color _connecting_color = Color.FromArgb(128, 128, 0);
        bool _stateChanged;

        private void Init(LiveSplitState state, USB2SnesW.USB2SnesW usb2snesw)
        {
            _state = state;
            _mystate = MyState.NONE;
            _proto_state = ProtocolState.NONE;
            _settings = new ComponentSettings();
            _model = new TimerModel() { CurrentState = _state };
            _state.RegisterTimerModel(_model);
            _stateChanged = false;
            _splits = new List<Split>();
            _inTimer = false;
            _config_checked = false;
            _valid_config = false;

            _update_timer = new Timer() { Interval = 1000 };
            _update_timer.Tick += (sender, args) => UpdateSplitsWrapper();
            _update_timer.Enabled = true;

            _state.OnReset += _state_OnReset;
            _state.OnStart += _state_OnStart;
            HorizontalWidth = 3;
            VerticalHeight = 3;
            _usb2snes = usb2snesw;
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
            Debug.WriteLine("Setting state to " + state);
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
                    Debug.WriteLine("Could not find the device : " + _settings.Device + " . Check your configuration or activate your device.");
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

        private bool ReadConfig()
        {
            try
            {
                var jsonStr = File.ReadAllText(_settings.ConfigFile);
                _game = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Game>(
                    jsonStr
                );
            }
            catch (Exception e)
            {
                Debug.WriteLine("Could not open split config file, check config file settings. " + e.Message);
                return false;
            }
            if (!this.CheckSplitsSetting())
            {
                ShowMessage("The split config file has missing definitions.");
                return false;
            }

            return true;
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
                        ShowMessage(String.Format("Split definition missing: {0} for category {1}", s, c.Name));
                        r = false;
                    }
                }
            }

            return r;
        }

        private bool CheckRunnableSetting()
        {
            List<String> splits = new List<string>(_game.Categories.Where(c => c.Name.ToLower() == _state.Run.CategoryName.ToLower()).First()?.Splits);

            if (splits.Count == 0)
            {
                ShowMessage("There are no splits for the current category in the split config file, check that the run category is correctly set and exists in the config file.");
                return false;
            }
            if (_state.Run.Count() > splits.Count())
            {
                ShowMessage(String.Format("There are more segments in your splits configuration <{0}> than the Autosplitter setting file <{1}>", _splits.Count(), _state.Run.Count()));
                return false;
            }

            return true;
        }

        // Let's build the split list based on the user segment list and not the category definition
        private void SetSplitList()
        {
            _splits?.Clear();
            var catSplits = _game.Categories.Where(c => c.Name.ToLower() == _state.Run.CategoryName.ToLower()).First().Splits;

            _splits = catSplits.Select(Name => _game.Definitions.Where(s => s.Name.ToLower() == Name.ToLower()).First()).ToList();
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

        public async void DoSplit()
        {
            _model.Split();
        }

        private bool IsConnectionReady()
        {
            // Debug.WriteLine("Checking connection");
            if (_proto_state == ProtocolState.ATTACHED)
                return true;

            // this method actually does a BLOCKING request-response cycle (!!)
            bool connected = _usb2snes.Connected();
            if (!connected)
            {
                SetState(MyState.NONE);
                _proto_state = ProtocolState.NONE;
            }
            this.Connect();
            return false;
        }

        private async void UpdateSplitsWrapper()
        {

            // Debug.WriteLine("Timer tick " + DateTime.Now);
            // "_inTimer" is a very questionable attempt at locking, but it's probably fine here.
            if (_inTimer)
            {
                Debug.WriteLine("In timer already! !!!");
                return;
            }
            _inTimer = true;
            try
            {
                await UpdateSplits();
            }  catch (Exception e)
            {
                Debug.WriteLine("Something bad happened: " + e.ToString());
                Connect();
            } finally {
                _inTimer = false;
            }
        }

        public async Task UpdateSplits()
        {
            if (_proto_state != ProtocolState.ATTACHED)
            {
                Connect();
                return;
            }

            if (!IsConfigReady())
            {
                return;
            }
            if (!IsConnectionReady())
            {
                _update_timer.Interval = 1000;
                return;
            }
            else
            {
                _update_timer.Interval = 33;
            }

            if (_game == null)
            {
                return;
            }

            await CheckSplits();
        }

        private bool IsConfigReady()
        {
            if (_state.Layout.HasChanged)
            {
                _config_checked = false;
            }
            if (!_config_checked)
            {
                if (this.ReadConfig())
                {
                    if (_config_checked == false && CheckRunnableSetting())
                    {
                        try
                        {
                            SetSplitList();
                            SetAutostart();
                            _valid_config = true;

                            Debug.WriteLine($"{_splits.Count} splits detected:");
                            foreach (var split in _splits)
                            {
                                Debug.WriteLine($"- {split.Name}");
                            }
                        } catch(Exception e)
                        {
                            Debug.WriteLine("Splits could not be parsed: " + e.ToString());
                            return false;
                        }
                    }
                }
                _config_checked = true;
            }
            if (!_valid_config)
            { 
                return false;
            }
            return true;
        }

        private async Task CheckSplits()
        {
            Split split;
            if (_state.CurrentPhase == TimerPhase.NotRunning && _autostart != null)
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
            if (split.Next != null && split.PosToCheck != 0)
            {
                split = split.Next[split.PosToCheck - 1];
            }
            bool ok = await CheckSplit(split);
            if (orignSplit.Next != null && ok)
            {
                Debug.WriteLine("Next count :" + orignSplit.Next.Count + " - Pos to check : " + orignSplit.PosToCheck);
                if (orignSplit.PosToCheck < orignSplit.Next.Count())
                {
                    orignSplit.PosToCheck++;
                    ok = false;
                }
                else
                {
                    orignSplit.PosToCheck = 0;
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
                    DoSplit();
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
            return split.Check(data);
        }

        public void DrawHorizontal(Graphics g, LiveSplitState state, float height, Region clipRegion)
        {
            VerticalHeight = height;
            HorizontalWidth = 3;
        }

        public void DrawVertical(Graphics g, LiveSplitState state, float width, Region clipRegion)
        {
            VerticalHeight = 3 + PaddingTop + PaddingBottom;
            HorizontalWidth = width;
            Color col;
            Console.WriteLine(_mystate);
            switch (_mystate)
            {
                case MyState.READY: col = _ok_color; break;
                case MyState.CONNECTING: col = _connecting_color; break;
                default: col = _error_color; break;
            }
            Brush b = new SolidBrush(col);
            g.FillRectangle(b, 0, 0, width, 3);
        }
    }
}
