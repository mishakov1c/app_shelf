// Standalone widget example. All prices are simulated and not market data.
using System;
using System.Globalization;
using System.IO;
using AppShelf;

namespace AppShelf.Sample
{
    internal static class StocksPlugin
    {
        private const string Id = "example.stocks";
        private const string Version = "1.0.0";
        private const string Demo = "Демо · условные котировки";
        private static int tick;

        private static PluginWidgetElement Text(string value)
        { return new PluginWidgetElement { Type = "text", Label = value }; }

        private static PluginWidgetElement Quote(string symbol, decimal baseline, int offset)
        {
            decimal change = ((tick + offset) % 9 - 4) * 0.25m;
            decimal price = baseline * (1 + change / 100);
            return new PluginWidgetElement { Type = "metric", Label = symbol,
                Value = String.Format(CultureInfo.InvariantCulture, "${0:F2} ({1:+0.00;-0.00;0.00}%)", price, change) };
        }

        private static PluginWidgetElement Action()
        { return new PluginWidgetElement { Type = "action", Label = "Следующий пример", CommandId = "next" }; }

        private static PluginWidgetLayout Layout(string size, params PluginWidgetElement[] elements)
        { return new PluginWidgetLayout { Size = size, Elements = elements }; }

        private static PluginWidget[] Snapshot()
        {
            tick = (tick + 1) % 9;
            PluginWidgetElement aapl = Quote("AAPL", 200, 0);
            PluginWidgetElement msft = Quote("MSFT", 400, 3);
            PluginWidgetElement nvda = Quote("NVDA", 150, 6);
            PluginWidgetElement amd = Quote("AMD", 120, 1);
            return new[]
            {
                new PluginWidget { Id = "quotes", Title = "Курсы акций · демо", Kind = "card",
                    DefaultSize = "full", Elements = new[] { Text(Demo), aapl, msft, nvda, amd, Action() },
                    Layouts = new[] {
                        Layout("full", Text(Demo), aapl, msft, nvda, amd, Action()),
                        Layout("half-horizontal", aapl, msft, nvda),
                        Layout("half-vertical", Text(Demo), aapl, msft, nvda),
                        Layout("quarter", aapl)
                    } },
                new PluginWidget { Id = "aapl", Title = "Apple · демо", Kind = "tile",
                    DefaultSize = "quarter", Elements = new[] { aapl },
                    Layouts = new[] { Layout("quarter", aapl), Layout("half-horizontal", aapl, Text(Demo)) } },
                new PluginWidget { Id = "market-demo", Title = "Акции · демо", Kind = "indicator",
                    Elements = new[] { aapl } }
            };
        }

        private static int Main()
        {
            try
            {
                using (Stream input = Console.OpenStandardInput())
                using (Stream output = Console.OpenStandardOutput())
                {
                    bool ready = false;
                    int previousId = 0;
                    while (true)
                    {
                        PluginRequest request = PluginFrame.Read<PluginRequest>(input);
                        if (request.ApiVersion != 1 || request.Id <= previousId ||
                            (!ready && request.Method != "hello") || (ready && request.Method == "hello"))
                            return 1;
                        previousId = request.Id;
                        PluginResponse reply = new PluginResponse { ApiVersion = 1, Id = request.Id, Ok = true };
                        switch (request.Method)
                        {
                            case "hello":
                                if (request.PluginId != Id || request.PluginVersion != Version)
                                    return 1;
                                reply.PluginId = Id; reply.PluginVersion = Version;
                                reply.Commands = new[] { new PluginCommand { Id = "next", Label = "Следующий пример" } };
                                ready = true;
                                break;
                            case "getWidgets": reply.Widgets = Snapshot(); break;
                            case "invoke":
                                if (request.CommandId == "next") tick = (tick + 1) % 9;
                                else { reply.Ok = false; reply.Error = "Unknown demo command."; }
                                break;
                            case "ping":
                            case "shutdown": break;
                            default: reply.Ok = false; reply.Error = "Unknown demo method."; break;
                        }
                        PluginFrame.Write(output, reply);
                        if (request.Method == "shutdown") return 0;
                    }
                }
            }
            catch (EndOfStreamException) { return 0; }
            catch (Exception) { return 1; }
        }
    }
}
