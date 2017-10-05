﻿using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using EasyHook;
using System.IO;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using IniParser;
using System.Text;
using IniParser.Model;

namespace Translator
{
    class Program
    {
        static string TargetLang = "";

        static void Main(string[] args)
        {
            var parser = new FileIniDataParser();
            IniData data = parser.ReadFile("Config.ini");
            TargetLang = data["Translation"]["lang"];

            Console.WriteLine("Welcome to DotA 2 Translator");
            Console.WriteLine("Translating to: " + TargetLang);

            int targetPID = 0;
            Process[] processes = Process.GetProcesses();
            for (int i = 0; i < processes.Length; i++)
            {
                try
                {
                    if (processes[i].MainWindowTitle == "Dota 2" && processes[i].HasExited == false)
                    {
                        targetPID = processes[i].Id;
                    }
                }
                catch { }
            }
            if (targetPID == 0)
            {
                Console.WriteLine("You see, you need to run the game for the translator to do something");
                Console.ReadKey();
                Environment.Exit(-1);
            }

            // Pipe 1 is used for game->translator communication
            // Pipe 2 is used for translator->game communication
            // I was having concurrency issues with using a single pipe
            var server1 = new NamedPipeServerStream("DotATranslator1");
            var server2 = new NamedPipeServerStream("DotATranslator2");

            try
            {
                NativeAPI.RhInjectLibrary(targetPID, 0, NativeAPI.EASYHOOK_INJECT_DEFAULT, "", ".\\Injectee.dll", IntPtr.Zero, 0);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                Console.Read();
                Environment.Exit(-1);
            }

            ListenPipeAndTranslate(server1, server2);
            Console.Read();
        }

        static async void ListenPipeAndTranslate(NamedPipeServerStream server1, NamedPipeServerStream server2)
        {
            var sr = new StreamReader(server1);
            do
            {
                try
                {
                    await server1.WaitForConnectionAsync();
                    await server2.WaitForConnectionAsync();
                    Console.WriteLine("GLHF!");
                    char[] buffer = new char[1000];
                    while (true)
                    {
                        System.Text.StringBuilder sb = new System.Text.StringBuilder();
                        await sr.ReadAsync(buffer, 0, 1000);
                        int endIdx = 0;
                        for (int i = 0; i < 1000; i++)
                        {
                            if (buffer[i] == '\0')
                            {
                                endIdx = i;
                                break;
                            }
                        }
                        sb.Append(buffer, 0, endIdx + 1);
                        TranslateAndPrint(sb.ToString(), server2);
                    }
                }
                catch (Exception)
                {
                    Console.WriteLine("Injectee disconnected, exiting");
                    Environment.Exit(0);
                }
                finally
                {
                    server1.WaitForPipeDrain();
                    if (server1.IsConnected) { server1.Disconnect(); }
                    server2.WaitForPipeDrain();
                    if (server2.IsConnected) { server2.Disconnect(); }
                }
            } while (true);
        }

        static async void TranslateAndPrint(string message, NamedPipeServerStream sw)
        {
            // These messages are already in the user's languages
            // They contain HTML too, so I'm not gonna bother translate them
            if (message.Contains("<img") || message.Contains("<font")) { return; }

            try
            {
                string url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=" + TargetLang + "&dt=t&q=" + WebUtility.UrlEncode(message);
                HttpClient hc = new HttpClient();
                HttpResponseMessage r = await hc.GetAsync(url);
                String rs = await r.Content.ReadAsStringAsync();
                dynamic d = JArray.Parse(rs);
                string translated = d[0][0][0];
                string sourcelang = d[2];
                string toSend = "(Translated from " + sourcelang + ") " + translated;
                if (sourcelang != TargetLang)
                {
                    UnicodeEncoding ue = new UnicodeEncoding(false, false, false);
                    byte[] sendBytes = ue.GetBytes(toSend);
                    byte[] size = new byte[1];
                    size[0] = Convert.ToByte(sendBytes.Length);
                    sw.Write(size, 0, 1);
                    sw.Write(sendBytes, 0, sendBytes.Length);
                    sw.Flush();
                    Console.WriteLine(toSend);
                }
            }
            catch (Exception e)
            {
                // If you close DotA 2 before closing the translator
                Console.Write("Unable to translate, check your connection: ");
                Console.WriteLine(e.Message);
                return;
            }
        }
    }
}
