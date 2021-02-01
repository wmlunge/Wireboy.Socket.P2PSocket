﻿using P2PSocket.Core.Commands;
using P2PSocket.Core.Models;
using P2PSocket.Core.Extends;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Net.Sockets;
using P2PSocket.Client.Models.Receive;
using P2PSocket.Client.Utils;
using P2PSocket.Core.Utils;
using System.Linq;
using System.Threading.Tasks;
using System.Net;
using System.Threading;

namespace P2PSocket.Client.Commands
{
    [CommandFlag(Core.P2PCommandType.P2P0x0201)]
    public class Cmd_0x0201 : P2PCommand
    {
        protected readonly P2PTcpClient m_tcpClient;
        protected TcpCenter tcpCenter = EasyInject.Get<TcpCenter>();
        protected AppConfig appCenter = EasyInject.Get<AppCenter>().Config;
        protected BinaryReader data { get; }
        public Cmd_0x0201(P2PTcpClient tcpClient, byte[] data)
        {
            m_tcpClient = tcpClient;
            this.data = new BinaryReader(new MemoryStream(data));
        }
        public override bool Excute()
        {
            int step = data.ReadInt32();
            LogUtils.Trace($"开始处理消息：0x0201 step:{step}");
            switch (step)
            {
                case 2:
                    {
                        bool isDestClient = BinaryUtils.ReadBool(data);
                        string token = BinaryUtils.ReadString(data);
                        int p2pType = BinaryUtils.ReadInt(data);
                        if (tcpCenter.WaiteConnetctTcp.ContainsKey(token))
                        {
                            tcpCenter.WaiteConnetctTcp[token].P2PType = p2pType;
                        }
                        if (p2pType == 0)
                        {
                            if (isDestClient) CreateTcpFromDest(token);
                            else CreateTcpFromSource(token);
                        }
                        else
                        {
                            if (isDestClient) CreateTcpFromDest_DirectConnect(token);
                            else CreateTcpFromSource_DirectConnect(token);
                        }
                    }
                    break;
                case 4:
                    ListenPort();
                    break;
                case 14:
                    TryBindP2PTcp();
                    break;
                case -1:
                    {
                        string message = BinaryUtils.ReadString(data);
                        LogUtils.Debug($"命令：0x0201 建立隧道失败，错误消息：{Environment.NewLine}{message}");
                    }
                    break;
            }
            return true;
        }

        protected virtual void TryBindP2PTcp()
        {
            string ip = BinaryUtils.ReadString(data);
            int port = BinaryUtils.ReadInt(data);
            string token = BinaryUtils.ReadString(data);
            int bindPort = Convert.ToInt32(m_tcpClient.Client.LocalEndPoint.ToString().Split(':')[1]);
            P2PTcpClient p2pClient = new P2PTcpClient();
            p2pClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            EasyOp.Do(() =>
            {
                p2pClient.Client.Bind(new IPEndPoint(IPAddress.Any, bindPort));
            },
             () =>
             {
                 if (tcpCenter.WaiteConnetctTcp.ContainsKey(token))
                 {
                     int p2pMode = tcpCenter.WaiteConnetctTcp[token].P2PType;
                     if ((p2pMode & 1) > 0)
                     {
                         LogUtils.Debug($"命令：0x0201 P2P模式隧道，开始端口复用打洞");
                         int tryCount = 3;
                         while (!p2pClient.Connected && tryCount > 0)
                         {
                             EasyOp.Do(() =>
                             {
                                 p2pClient.Connect(ip, port);
                                 p2pClient.UpdateEndPoint();
                             }, ex =>
                             {
                                 LogUtils.Trace($"命令：0x0201 P2P模式隧道，端口打洞错误{ex}");
                             });
                             tryCount--;
                         }
                     }
                     if ((p2pMode & 2) > 0)
                     {
                         LogUtils.Debug($"命令：0x0201 P2P模式隧道，开始端口预测打洞");
                         p2pClient = TryRadomPort(ip, port);
                     }
                 }

                 if (p2pClient != null && p2pClient.Connected)
                 {
                     LogUtils.Debug($"命令：0x0201 P2P模式隧道，打洞成功 port:{bindPort} token:{token}");
                     P2PBind_DirectConnect(p2pClient, token);
                 }
                 else
                 {
                     EasyOp.Do(() => { p2pClient.SafeClose(); });
                     //如果是发起端，清空集合
                     if (m_tcpClient.P2PLocalPort <= 0)
                     {
                         if (tcpCenter.WaiteConnetctTcp.ContainsKey(token))
                         {
                             tcpCenter.WaiteConnetctTcp[token].ErrorMsg = $"命令：0x0201 P2P模式隧道，打洞失败 token:{token}";
                             tcpCenter.WaiteConnetctTcp[token].PulseBlock();
                         }
                     }
                     else
                     {
                         LogUtils.Debug($"命令：0x0201 P2P模式隧道，打洞失败 token:{token}");
                     }
                 }
                 EasyOp.Do(() => { m_tcpClient.SafeClose(); });
             },
             ex =>
             {
                 //如果是发起端，清空集合
                 if (m_tcpClient.P2PLocalPort <= 0)
                 {
                     if (tcpCenter.WaiteConnetctTcp.ContainsKey(token))
                     {
                         tcpCenter.WaiteConnetctTcp[token].ErrorMsg = $"命令：0x0201 P2P模式隧道，端口复用失败 token:{token}:{Environment.NewLine}{ex}";
                         tcpCenter.WaiteConnetctTcp[token].PulseBlock();
                     }
                 }
                 else
                 {
                     LogUtils.Debug($"命令：0x0201 P2P模式隧道，端口复用失败 token:{token}:{Environment.NewLine}{ex}");
                 }
             });
        }

        protected virtual P2PTcpClient TryRadomPort(string ip, int port)
        {
            P2PTcpClient ret = null;
            AsyncCallback action = ar =>
            {
                P2PTcpClient tcp = ar.AsyncState as P2PTcpClient;
                EasyOp.Do(() => tcp.EndConnect(ar), () =>
                {
                    if (tcp != null && tcp.Connected)
                    {
                        ret = tcp;
                    }
                }, ex =>
                {
                    LogUtils.Trace($"{ex.Message}");
                });
            };
            List<P2PTcpClient> tcpList = new List<P2PTcpClient>();
            for (int i = 1; i <= 8; i++)
            {
                P2PTcpClient tcp = new P2PTcpClient();
                tcpList.Add(tcp);
                tcp.BeginConnect(ip, port + 6, action, tcp);
            }
            int cTime = 0;
            while (ret == null && cTime < 3000)
            {
                Thread.Sleep(100);
                cTime += 100;
            }
            tcpList.ForEach(t =>
            {
                if (t != ret)
                {
                    EasyOp.Do(() => t.Close());
                    EasyOp.Do(() => t.Dispose());
                }
            });
            return ret;
        }

        protected virtual void P2PBind_DirectConnect(P2PTcpClient p2pClient, string token)
        {
            if (m_tcpClient.P2PLocalPort > 0)
            {
                //B端
                int port = m_tcpClient.P2PLocalPort;
                PortMapItem destMap = appCenter.PortMapList.FirstOrDefault(t => t.LocalPort == port && string.IsNullOrEmpty(t.LocalAddress));

                P2PTcpClient portClient = null;

                EasyOp.Do(() =>
                {
                    if (destMap != null)
                        if (destMap.MapType == PortMapType.ip)
                            portClient = new P2PTcpClient(destMap.RemoteAddress, destMap.RemotePort);
                        else
                            portClient = new P2PTcpClient("127.0.0.1", port);
                    else
                        portClient = new P2PTcpClient("127.0.0.1", port);
                },
                () =>
                {
                    portClient.IsAuth = p2pClient.IsAuth = true;
                    portClient.ToClient = p2pClient;
                    p2pClient.ToClient = portClient;
                    EasyOp.Do(() =>
                    {
                        if (Global_Func.BindTcp(p2pClient, portClient))
                        {
                            LogUtils.Debug($"命令：0x0201 P2P模式隧道，连接成功 token:{token}");
                        }
                        else
                        {
                            LogUtils.Debug($"命令：0x0201 P2P模式隧道，连接失败 token:{token}");
                        }
                    },
                    ex =>
                    {
                        LogUtils.Debug($"命令：0x0201 P2P模式隧道,连接失败 token:{token}：{Environment.NewLine}{ex}");
                    });
                },
                ex =>
                {
                    LogUtils.Debug($"命令：0x0201 P2P模式隧道,连接目标端口失败 token:{token}：{Environment.NewLine}{ex}");
                    EasyOp.Do(() => { p2pClient.SafeClose(); });
                });


            }
            else
            {
                tcpCenter.WaiteConnetctTcp[token].Tcp = p2pClient;
                tcpCenter.WaiteConnetctTcp[token].PulseBlock();
            }
        }

        protected virtual void CreateTcpFromDest_DirectConnect(string token)
        {
            int port = BinaryUtils.ReadInt(data);
            Utils.LogUtils.Debug($"命令：0x0201  正尝试建立P2P模式隧道 token:{token}");
            P2PTcpClient serverClient = new P2PTcpClient();
            serverClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            EasyOp.Do(() =>
            {
                serverClient.Connect(appCenter.ServerAddress, appCenter.ServerPort);
            },
            () =>
            {
                serverClient.IsAuth = true;
                serverClient.P2PLocalPort = port;
                serverClient.UpdateEndPoint();
                Models.Send.Send_0x0201_Bind sendPacket = new Models.Send.Send_0x0201_Bind(token);

                EasyOp.Do(() =>
                {
                    serverClient.BeginSend(sendPacket.PackData());
                },
                () =>
                {
                    EasyOp.Do(() =>
                    {
                        Global_Func.ListenTcp<ReceivePacket>(serverClient);
                        LogUtils.Debug($"命令：0x0201 P2P模式隧道，已连接到服务器，等待下一步操作 token:{token}");
                    }, ex =>
                    {
                        LogUtils.Debug($"命令：0x0201 P2P模式隧道，服务器连接被强制断开 token:{token}：{Environment.NewLine}{ex}");
                        EasyOp.Do(() => { serverClient.SafeClose(); });
                    });
                }, ex =>
                {
                    LogUtils.Debug($"命令：0x0201 P2P模式隧道，隧道打洞失败 token:{token}：{Environment.NewLine} 隧道被服务器强制断开");
                });
            }, ex =>
            {
                LogUtils.Debug($"命令：0x0201 P2P模式隧道，无法连接服务器 token:{token}：{Environment.NewLine}{ex}");
            });


        }
        protected virtual void CreateTcpFromSource_DirectConnect(string token)
        {
            Models.Send.Send_0x0201_Bind sendPacket = new Models.Send.Send_0x0201_Bind(token);
            Utils.LogUtils.Debug($"命令：0x0201  正尝试建立P2P模式隧道 token:{token}");
            if (tcpCenter.WaiteConnetctTcp.ContainsKey(token))
            {
                P2PTcpClient serverClient = new P2PTcpClient();
                serverClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                EasyOp.Do(() =>
                {
                    serverClient.Connect(appCenter.ServerAddress, appCenter.ServerPort);
                }, () =>
                {
                    serverClient.IsAuth = true;
                    serverClient.UpdateEndPoint();
                    EasyOp.Do(() =>
                    {
                        serverClient.BeginSend(sendPacket.PackData());
                    },
                    () =>
                    {
                        EasyOp.Do(() =>
                        {
                            Global_Func.ListenTcp<ReceivePacket>(serverClient);
                            LogUtils.Debug($"命令：0x0201 P2P模式隧道，已连接到服务器，等待下一步操作 token:{token}");
                        }, ex =>
                        {
                            tcpCenter.WaiteConnetctTcp[token].ErrorMsg = $"命令：0x0201 P2P模式隧道，服务器连接被强制断开 token:{token}：{Environment.NewLine}{ex}";
                            tcpCenter.WaiteConnetctTcp[token].PulseBlock();
                        });
                    }, ex =>
                    {
                        tcpCenter.WaiteConnetctTcp[token].ErrorMsg = $"命令：0x0201 P2P模式隧道，隧道打洞失败 token:{token}：{Environment.NewLine} 隧道被服务器强制断开";
                        tcpCenter.WaiteConnetctTcp[token].PulseBlock();
                    });
                }, ex =>
                {
                    tcpCenter.WaiteConnetctTcp[token].ErrorMsg = $"命令：0x0201 P2P模式隧道，无法连接服务器 token:{token}：{Environment.NewLine}{ex}";
                    tcpCenter.WaiteConnetctTcp[token].PulseBlock();
                });
            }
            else
            {
                LogUtils.Debug($"命令：0x0201 接收到建立P2P模式隧道命令，但已超时. token:{token}");
            }
        }

        /// <summary>
        ///     从目标端创建与服务器的tcp连接
        /// </summary>
        /// <param name="token"></param>
        protected virtual void CreateTcpFromDest(string token)
        {
            Utils.LogUtils.Debug($"命令：0x0201  正在连接中转模式隧道通道 token:{token}");
            int port = BinaryUtils.ReadInt(data);
            PortMapItem destMap = appCenter.PortMapList.FirstOrDefault(t => t.LocalPort == port && string.IsNullOrEmpty(t.LocalAddress));


            P2PTcpClient portClient = null;
            EasyOp.Do(() =>
            {
                if (destMap != null)
                    if (destMap.MapType == PortMapType.ip)
                        portClient = new P2PTcpClient(destMap.RemoteAddress, destMap.RemotePort);
                    else
                        portClient = new P2PTcpClient("127.0.0.1", port);
                else
                    portClient = new P2PTcpClient("127.0.0.1", port);
            }, () =>
            {
                P2PTcpClient serverClient = null;
                EasyOp.Do(() =>
                {
                    serverClient = new P2PTcpClient(appCenter.ServerAddress, appCenter.ServerPort);
                }, () =>
                {
                    portClient.IsAuth = serverClient.IsAuth = true;
                    portClient.ToClient = serverClient;
                    serverClient.ToClient = portClient;
                    Models.Send.Send_0x0201_Bind sendPacket = new Models.Send.Send_0x0201_Bind(token);

                    EasyOp.Do(() =>
                    {
                        serverClient.BeginSend(sendPacket.PackData());
                    }, () =>
                    {
                        EasyOp.Do(() =>
                        {
                            Global_Func.ListenTcp<ReceivePacket>(serverClient);
                            Global_Func.ListenTcp<Packet_0x0202>(portClient);
                            Utils.LogUtils.Debug($"命令：0x0201  中转模式隧道，连接成功 token:{token}");
                        }, ex =>
                        {
                            LogUtils.Debug($"命令：0x0201 中转模式隧道,连接失败 token:{token}：{Environment.NewLine}{ex}");
                            EasyOp.Do(() => { serverClient.SafeClose(); });
                            EasyOp.Do(() => { portClient.SafeClose(); });

                        });
                    }, ex =>
                    {
                        LogUtils.Debug($"命令：0x0201 中转模式隧道,连接失败 token:{token}：{Environment.NewLine}{ex}");
                        EasyOp.Do(() => { portClient.SafeClose(); });
                    });
                }, ex =>
                {
                    LogUtils.Debug($"命令：0x0201 中转模式隧道,连接失败 token:{token}：{Environment.NewLine}{ex}");
                    EasyOp.Do(() => { portClient.SafeClose(); });
                });
            }, ex =>
            {
                LogUtils.Debug($"命令：0x0201 中转模式隧道,连接目标端口失败 token{token}：{Environment.NewLine}{ex}");
            });
        }

        /// <summary>
        ///     从发起端创建与服务器的tcp连接
        /// </summary>
        /// <param name="token"></param>
        protected virtual void CreateTcpFromSource(string token)
        {
            Utils.LogUtils.Debug($"命令：0x0201  正尝试建立中转模式隧道token:{token}");
            if (tcpCenter.WaiteConnetctTcp.ContainsKey(token))
            {
                P2PTcpClient serverClient = null;
                EasyOp.Do(() =>
                {
                    serverClient = new P2PTcpClient(appCenter.ServerAddress, appCenter.ServerPort);
                }, () =>
                {

                    Models.Send.Send_0x0201_Bind sendPacket = new Models.Send.Send_0x0201_Bind(token);
                    EasyOp.Do(() =>
                    {
                        serverClient.BeginSend(sendPacket.PackData());
                    }, () =>
                    {
                        EasyOp.Do(() =>
                        {
                            tcpCenter.WaiteConnetctTcp[token].Tcp = serverClient;
                            Global_Func.ListenTcp<ReceivePacket>(serverClient);
                            //Utils.LogUtils.Debug($"命令：0x0201  中转模式隧道,隧道建立并连接成功 token:{token}");
                        }, ex =>
                        {
                            tcpCenter.WaiteConnetctTcp[token].ErrorMsg = $"命令：0x0201  中转模式隧道,隧道建立失败 token:{token}：{Environment.NewLine} {ex}";
                            tcpCenter.WaiteConnetctTcp[token].PulseBlock();
                        });
                    }, ex =>
                    {
                        tcpCenter.WaiteConnetctTcp[token].ErrorMsg = $"命令：0x0201  中转模式隧道,隧道建立失败 token:{token}：{Environment.NewLine} 隧道被服务器强制断开";
                        tcpCenter.WaiteConnetctTcp[token].PulseBlock();
                    });



                }, ex =>
                {
                    tcpCenter.WaiteConnetctTcp[token].ErrorMsg = $"命令：0x0201  中转模式隧道,无法连接服务器 token:{token}：{Environment.NewLine}{ex}";
                    tcpCenter.WaiteConnetctTcp[token].PulseBlock();
                });

            }
            else
            {
                LogUtils.Debug($"命令：0x0201 接收到建立中转模式隧道命令，但已超时. token:{token}");
            }
        }

        /// <summary>
        ///     监听连接外部程序的端口
        /// </summary>
        protected virtual void ListenPort()
        {
            if (data.PeekChar() >= 0)
            {
                string token = BinaryUtils.ReadString(data);
                if (tcpCenter.WaiteConnetctTcp.ContainsKey(token))
                {
                    EasyOp.Do(() =>
                    {
                        tcpCenter.WaiteConnetctTcp[token].PulseBlock();
                    }, ex =>
                    {
                        tcpCenter.WaiteConnetctTcp[token].ErrorMsg = $"命令：0x0201  隧道连接失败,源Tcp连接已断开：{Environment.NewLine}{ex}";
                        tcpCenter.WaiteConnetctTcp[token].PulseBlock();
                    });
                }
            }
            else
            {
                Utils.LogUtils.Debug($"命令：0x0201  隧道连接失败,服务端版本过低");
            }
        }
    }
}
