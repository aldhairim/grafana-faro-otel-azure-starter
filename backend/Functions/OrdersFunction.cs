using System.Diagnostics;
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace PortfolioApi.Functions;

// POST /orders — a write flow. Opens a "risk.check" child span, increments a custom metric,
// and supports ?fail=true to force a failure so you can see error traces + error logs.
public class OrdersFunction
{
    private readonly ILogger _log;
    public OrdersFunction(ILoggerFactory lf) => _log = lf.CreateLogger<OrdersFunction>();

    public record OrderRequest(string Ticker, int Quantity);

    [Function("PlaceOrder")]
    public Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "orders")] HttpRequestData req)
        => Telemetry.Handle(req, _log, "POST /orders", async span =>
        {
            var order = await req.ReadFromJsonAsync<OrderRequest>() ?? new OrderRequest("UNKNOWN", 0);
            span?.SetTag("order.ticker", order.Ticker);
            span?.SetTag("order.quantity", order.Quantity);
            _log.LogInformation("Placing order {Qty} x {Ticker}", order.Quantity, order.Ticker);

            using (var risk = Telemetry.Source.StartActivity("risk.check", ActivityKind.Internal))
            {
                risk?.SetTag("risk.rule", "position-limit");
                await Task.Delay(Random.Shared.Next(15, 60));
                // ?fail=true simulates a downstream failure -> error span + error log.
                if (req.Url.Query.Contains("fail=true"))
                    throw new InvalidOperationException("Risk check failed: position limit exceeded");
            }

            Telemetry.OrdersPlaced.Add(1, new KeyValuePair<string, object?>("ticker", order.Ticker));

            var res = req.CreateResponse(HttpStatusCode.OK);
            await res.WriteAsJsonAsync(new
            {
                status = "accepted",
                orderId = Guid.NewGuid().ToString("n")[..12],
                order,
                asOf = DateTime.UtcNow,
            });
            return res;
        });
}
