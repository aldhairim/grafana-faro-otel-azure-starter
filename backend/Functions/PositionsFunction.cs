using System.Diagnostics;
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace PortfolioApi.Functions;

// GET /positions — a second read flow. Opens a "pricing.lookup" child span with a bit of
// simulated latency so traces have some variation to look at.
public class PositionsFunction
{
    private readonly ILogger _log;
    public PositionsFunction(ILoggerFactory lf) => _log = lf.CreateLogger<PositionsFunction>();

    private static readonly (string ticker, int shares, double last)[] Book =
    {
        ("FUND-A", 12000, 98.05), ("FUND-B", 8400, 52.10),
        ("FUND-C", 5030, 150.72), ("IDX-500", 2200, 410.15),
    };

    [Function("Positions")]
    public Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "positions")] HttpRequestData req)
        => Telemetry.Handle(req, _log, "GET /positions", async span =>
        {
            using (var pricing = Telemetry.Source.StartActivity("pricing.lookup", ActivityKind.Internal))
            {
                pricing?.SetTag("pricing.source", "eod-cache");
                pricing?.SetTag("pricing.count", Book.Length);
                await Task.Delay(Random.Shared.Next(20, 80)); // simulate a pricing call
            }

            _log.LogInformation("Returning {Count} positions", Book.Length);
            var res = req.CreateResponse(HttpStatusCode.OK);
            await res.WriteAsJsonAsync(new
            {
                count = Book.Length,
                positions = Book.Select(p => new { p.ticker, p.shares, marketValueUsd = Math.Round(p.shares * p.last, 2) }),
                asOf = DateTime.UtcNow,
            });
            return res;
        });
}
