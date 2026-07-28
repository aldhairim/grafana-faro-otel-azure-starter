using System.Diagnostics;
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace PortfolioApi.Functions;

// GET /portfolio — the primary "View Portfolio" flow. Returns a static summary and opens
// a nested child span so the trace shows server -> valuation, not just a single span.
public class PortfolioFunction
{
    private readonly ILogger _log;
    public PortfolioFunction(ILoggerFactory lf) => _log = lf.CreateLogger<PortfolioFunction>();

    [Function("Portfolio")]
    public Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "portfolio")] HttpRequestData req)
        => Telemetry.Handle(req, _log, "GET /portfolio", async span =>
        {
            _log.LogInformation("Loading portfolio summary");

            using (var valuation = Telemetry.Source.StartActivity("valuation.compute", ActivityKind.Internal))
            {
                valuation?.SetTag("valuation.method", "mark-to-market");
                await Task.Delay(Random.Shared.Next(10, 40)); // simulate work
            }

            var res = req.CreateResponse(HttpStatusCode.OK);
            await res.WriteAsJsonAsync(new
            {
                account = "DEMO-001",
                totalValueUsd = 2_629_850.53,
                holdings = new[]
                {
                    new { ticker = "FUND-A", valueUsd = 1_284_530.11 },
                    new { ticker = "FUND-B", valueUsd = 842_100.55 },
                    new { ticker = "FUND-C", valueUsd = 503_219.87 },
                },
                asOf = DateTime.UtcNow,
            });
            return res;
        });
}
