﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.ServiceProcess;
using System.Text;
using System.Threading;

namespace P2PSocket.StartUp_Windows
{
    partial class P2PSocket : ServiceBase
    {
        public P2PSocket()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                if (!StartServer(AppDomain.CurrentDomain))
                {
                    StartClient(AppDomain.CurrentDomain);
                }
            }
            catch (Exception ex)
            {
                StreamWriter ss = new StreamWriter($"{AppDomain.CurrentDomain.BaseDirectory}P2PSocket/Error.log");
                ss.WriteLine(ex);
                ss.Close();
                throw ex;
            }
        }

        protected override void OnStop()
        {
            Environment.Exit(0);
        }
        public static bool StartClient(AppDomain appDomain)
        {
            bool ret = false;

            if (File.Exists($"{appDomain.BaseDirectory}P2PSocket/P2PSocket.Client.dll"))
            {
                Assembly assembly = Assembly.LoadFrom($"{appDomain.BaseDirectory}P2PSocket/P2PSocket.Client.dll");
                assembly = appDomain.Load(assembly.FullName);
                object obj = assembly.CreateInstance("P2PSocket.Client.CoreModule");
                MethodInfo method = obj.GetType().GetMethod("Start");
                method.Invoke(obj, null);
                ret = true;
            }
            return ret;
        }
        public static bool StartServer(AppDomain appDomain)
        {
            bool ret = false;
            if (File.Exists($"{appDomain.BaseDirectory}P2PSocket/P2PSocket.Server.dll"))
            {
                Assembly assembly = Assembly.LoadFrom($"{appDomain.BaseDirectory}P2PSocket/P2PSocket.Server.dll");
                assembly = appDomain.Load(assembly.FullName);
                object obj = assembly.CreateInstance("P2PSocket.Server.CoreModule");
                MethodInfo method = obj.GetType().GetMethod("Start");
                method.Invoke(obj, null);
                ret = true;
            }
            return ret;
        }
    }
}
