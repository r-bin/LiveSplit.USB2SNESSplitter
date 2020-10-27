﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Threading;
using WebSocketSharp;
using System.Diagnostics;

namespace USB2SnesW
{
    internal class USB2SnesW
    {
        class USRequest
        {
            public String Opcode;
            public String Space;
            public List<String> Operands;
        }
        public class USInfo
        {
            public String version;
            public String romPlaying;
            public List<String> flags;
        }
        class USReply
        {
            public List<string> Results { get; set; }
        }
        public enum Commands {
            DeviceList,
            Name,
            GetAddress,
            Reset,
            Attach,
            Info
        }
        private WebSocket ws;

        public USB2SnesW()
        {
            Console.WriteLine("Creating USB2Snes");
            ws = new WebSocket("ws://localhost:8080");
            Console.WriteLine(ws);
        }
        public async Task<bool> Connect()
        {
            if (ws.ReadyState == WebSocketState.Open)
            {
                return true;
            }

            var tcs = new TaskCompletionSource<bool>();
            EventHandler openHandler = null;
            EventHandler<ErrorEventArgs> errorHandler = null;
            EventHandler<CloseEventArgs> closeHandler = null;
            openHandler = (socket, eventArgs) => {
                ws.OnOpen -= openHandler;
                ws.OnClose -= closeHandler;
                ws.OnError -= errorHandler;

                tcs.TrySetResult(true);
            };
            errorHandler = (socket, eventArgs) => {
                ws.OnOpen -= openHandler;
                ws.OnClose -= closeHandler;
                ws.OnError -= errorHandler;

                tcs.TrySetResult(false);
            };
            closeHandler = (socket, eventArgs) => {
                ws.OnOpen -= openHandler;
                ws.OnClose -= closeHandler;
                ws.OnError -= errorHandler;

                tcs.TrySetResult(false);
            };
            ws.OnOpen += openHandler;
            ws.OnError += errorHandler;
            ws.OnClose += closeHandler;

            try
            {
                ws.ConnectAsync();
            } catch (Exception e)
            {
                Console.WriteLine("Exception: " + e);
                tcs.TrySetResult(false);
            }
            return await tcs.Task;
        }
        private void SendCommand(Commands cmd, String arg)
        {
            List<String> args = new List<string>();
            args.Add(arg);
            SendCommand(cmd, args);
        }
        private void SendCommand(Commands cmd, List<String> args)
        {
            USRequest req = new USRequest();
            req.Opcode = cmd.ToString();
            req.Space = "SNES";
            req.Operands = args;
            string json = new JavaScriptSerializer().Serialize(req);
            ws.Send(json);
        }
        
        public void SetName(String name)
        {
            SendCommand(Commands.Name, name);
        }

        private async Task<USReply> waitForReply()
        {
            var tcs = new TaskCompletionSource<USReply>();
            EventHandler<MessageEventArgs> msgHandler = null;
            EventHandler<ErrorEventArgs> errorHandler = null;
            EventHandler<CloseEventArgs> closeHandler = null;
            msgHandler = (s, e) => {
                ws.OnMessage -= msgHandler;
                ws.OnClose -= closeHandler;
                ws.OnError -= errorHandler;

                if (e.IsText)
                    tcs.TrySetResult(new JavaScriptSerializer().Deserialize<USReply>(e.Data));
                else
                    tcs.TrySetResult(new USReply());

            };
            errorHandler = (s, e) => {
                ws.OnMessage -= msgHandler;
                ws.OnClose -= closeHandler;
                ws.OnError -= errorHandler;

                tcs.TrySetCanceled();
            };
            
            closeHandler = (s, e) =>
            {
                ws.OnMessage -= msgHandler;
                ws.OnClose -= closeHandler;
                ws.OnError -= errorHandler;

                tcs.TrySetCanceled();
            };
            ws.OnMessage += msgHandler;
            ws.OnError += errorHandler;
            ws.OnClose += closeHandler;

            var ct = new CancellationTokenSource(2000);
            ct.Token.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false);

            return await tcs.Task;
        }
        public async Task<List<String>> GetDevices()
        {
            SendCommand(Commands.DeviceList, "");
            USReply rep = await waitForReply();
            return rep.Results;
      }
        public void Attach(String device)
        {
            SendCommand(Commands.Attach, device);
        }
  
        public async Task<byte[]> GetAddress(uint address, uint size)
        {
            List<String> args = new List<String>();
            args.Add(address.ToString("X"));
            args.Add(size.ToString("X"));
            SendCommand(Commands.GetAddress, args);
            var tcs = new TaskCompletionSource<byte[]>();
            EventHandler<MessageEventArgs> msgHandler = null;
            EventHandler<ErrorEventArgs> errorHandler = null;
            EventHandler<CloseEventArgs> closeHandler = null;
            msgHandler = (s, e) => {
                ws.OnMessage -= msgHandler;
                ws.OnClose -= closeHandler;
                ws.OnError -= errorHandler;

                if (e.IsBinary)
                    tcs.TrySetResult(e.RawData);
                else
                    tcs.TrySetResult(new byte[0]);
            };
            errorHandler = (s, e) => {
                ws.OnMessage -= msgHandler;
                ws.OnClose -= closeHandler;
                ws.OnError -= errorHandler;

                Console.WriteLine("Error in get address : " + e.Message);
                tcs.SetCanceled();
            };
            closeHandler = (s, e) =>
            {
                ws.OnMessage -= msgHandler;
                ws.OnClose -= closeHandler;
                ws.OnError -= errorHandler;

                Console.WriteLine("Connection closed");
                tcs.SetCanceled();
            };
            ws.OnMessage += msgHandler;
            ws.OnError += errorHandler;
            ws.OnClose += closeHandler;

            if (await Task.WhenAny(tcs.Task, Task.Delay(100)) == tcs.Task)
            {
                return tcs.Task.Result;
            } else {
                return new byte[0];
            }
        }
        public void Reset()
        {
            SendCommand(Commands.Reset, "");
        }
        public bool Connected()
        {
            bool live = ws.IsAlive;
            return live;
        }
        public void Disconnect()
        {
            ws.Close();
        }
        public async Task<USInfo> Info()
        {
            SendCommand(Commands.Info, "");
            USInfo info = new USInfo();
            try
            {
                USReply result = await waitForReply();
                
                info.version = result.Results[0];
                info.romPlaying = result.Results[2];
                info.flags = result.Results.Skip(3).ToList();
            } catch(Exception e) {
                Console.WriteLine(e.Message);
            }
            return info;
        }
    }
}
