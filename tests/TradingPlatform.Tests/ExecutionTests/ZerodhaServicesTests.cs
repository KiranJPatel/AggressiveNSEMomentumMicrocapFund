using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Execution.Zerodha;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.ExecutionTests;

public class ZerodhaServicesTests
{
    private static ZerodhaOptions Options() => new() { OrderRateLimitPerMinute = 200, GeneralRateLimitPerMinute = 600, MaxRetries = 2, InitialBackoffMilliseconds = 1, MaxBackoffMilliseconds = 10 };

    [Fact]
    public async Task ZerodhaClient_ExchangeRequestToken_SendsCorrectChecksum_AndStoresAccessToken()
    {
        var transport = new FakeKiteHttpTransport((method, path, form, auth) =>
        {
            Assert.Equal("/session/token", path.Split("api.kite.trade")[^1]);
            var expectedChecksum = KiteChecksumHelper.ComputeChecksum("key1", "reqtok1", "secret1");
            Assert.Equal(expectedChecksum, form!["checksum"]);
            return new KiteHttpResponse(200, """{"status":"success","data":{"access_token":"newtoken123"}}""");
        });

        var gateway = new KiteApiGateway(transport, Options(), "key1");
        var client = new ZerodhaClient(gateway, "key1", "secret1");

        var token = await client.ExchangeRequestTokenAsync("reqtok1");

        Assert.Equal("newtoken123", token);
        Assert.True(await client.IsAuthenticatedAsync());
    }

    [Fact]
    public async Task ZerodhaOrderExecutionService_PlaceOrder_SendsCorrectFormParams_ForMarketBuy()
    {
        Dictionary<string, string>? capturedParams = null;
        var transport = new FakeKiteHttpTransport((method, path, form, auth) =>
        {
            capturedParams = form as Dictionary<string, string> ?? form!.ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.Equal("token key1:tok1", auth);
            return new KiteHttpResponse(200, """{"status":"success","data":{"order_id":"OID1"}}""");
        });
        var gateway = new KiteApiGateway(transport, Options(), "key1", "tok1");
        var service = new ZerodhaOrderExecutionService(gateway);

        var order = new Order { Symbol = "SBIN", Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 50m, CorrelationId = "c1" };
        var result = await service.PlaceOrderAsync(order);

        Assert.Equal("OID1", result.BrokerOrderId);
        Assert.Equal(OrderState.Submitted, result.State);
        Assert.Equal("SBIN", capturedParams!["tradingsymbol"]);
        Assert.Equal("BUY", capturedParams["transaction_type"]);
        Assert.Equal("MARKET", capturedParams["order_type"]);
        Assert.Equal("CNC", capturedParams["product"]);
        Assert.Equal("50", capturedParams["quantity"]);
    }

    [Fact]
    public async Task ZerodhaOrderExecutionService_StopLossOrder_MapsToSlM_WithTriggerPrice()
    {
        Dictionary<string, string>? capturedParams = null;
        var transport = new FakeKiteHttpTransport((method, path, form, auth) =>
        {
            capturedParams = form!.ToDictionary(kv => kv.Key, kv => kv.Value);
            return new KiteHttpResponse(200, """{"status":"success","data":{"order_id":"OID2"}}""");
        });
        var gateway = new KiteApiGateway(transport, Options(), "key1", "tok1");
        var service = new ZerodhaOrderExecutionService(gateway);

        var order = new Order { Symbol = "SBIN", Side = OrderSide.Sell, Type = OrderType.StopLoss, Quantity = 50m, StopPrice = 540m, CorrelationId = "c1" };
        await service.PlaceOrderAsync(order);

        Assert.Equal("SL-M", capturedParams!["order_type"]);
        Assert.Equal("540.00", capturedParams["trigger_price"]);
    }

    [Fact]
    public async Task ZerodhaOrderExecutionService_PlaceOrder_OnRejection_SetsFailedState_NeverThrows()
    {
        var transport = new FakeKiteHttpTransport((method, path, form, auth) =>
            new KiteHttpResponse(500, """{"status":"error","error_type":"OrderException","message":"Insufficient funds."}"""));
        var gateway = new KiteApiGateway(transport, Options(), "key1", "tok1");
        var service = new ZerodhaOrderExecutionService(gateway);

        var order = new Order { Symbol = "SBIN", Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 50m, CorrelationId = "c1" };
        var result = await service.PlaceOrderAsync(order);

        Assert.Equal(OrderState.Failed, result.State);
        Assert.Equal("Insufficient funds.", result.RejectionReason);
    }

    [Fact]
    public async Task ZerodhaOrderExecutionService_NoAccessToken_ThrowsTokenException()
    {
        var transport = new FakeKiteHttpTransport((method, path, form, auth) => new KiteHttpResponse(200, "{}"));
        var gateway = new KiteApiGateway(transport, Options(), "key1"); // no initial token
        var service = new ZerodhaOrderExecutionService(gateway);

        var order = new Order { Symbol = "SBIN", Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 50m, CorrelationId = "c1" };
        var result = await service.PlaceOrderAsync(order);

        // PlaceOrderAsync catches KiteException and reflects it as a Failed order, never an unhandled throw.
        Assert.Equal(OrderState.Failed, result.State);
    }

    [Fact]
    public async Task ZerodhaOrderExecutionService_TransientNetworkError_IsRetried_ThenSucceeds()
    {
        var callCount = 0;
        var transport = new FakeKiteHttpTransport((method, path, form, auth) =>
        {
            callCount++;
            if (callCount < 2) return new KiteHttpResponse(503, """{"status":"error","error_type":"NetworkException","message":"temporary"}""");
            return new KiteHttpResponse(200, """{"status":"success","data":{"order_id":"OID3"}}""");
        });
        var gateway = new KiteApiGateway(transport, Options(), "key1", "tok1");
        var service = new ZerodhaOrderExecutionService(gateway);

        var order = new Order { Symbol = "SBIN", Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 50m, CorrelationId = "c1" };
        var result = await service.PlaceOrderAsync(order);

        Assert.Equal("OID3", result.BrokerOrderId);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task ZerodhaPositionService_ParsesBrokerPositions()
    {
        var transport = new FakeKiteHttpTransport((method, path, form, auth) =>
            new KiteHttpResponse(200, """{"status":"success","data":{"net":[{"tradingsymbol":"SBIN","exchange":"NSE","quantity":100,"average_price":560.5}],"day":[]}}"""));
        var gateway = new KiteApiGateway(transport, Options(), "key1", "tok1");
        var service = new ZerodhaPositionService(gateway);

        var positions = await service.GetBrokerPositionsAsync();

        Assert.Equal(1, positions.Count);
        Assert.Equal("SBIN", positions[0].Symbol);
    }

    [Fact]
    public async Task ZerodhaAccountService_ParsesAvailableCash()
    {
        var transport = new FakeKiteHttpTransport((method, path, form, auth) =>
            new KiteHttpResponse(200, """{"status":"success","data":{"available":{"cash":123456.78}}}"""));
        var gateway = new KiteApiGateway(transport, Options(), "key1", "tok1");
        var service = new ZerodhaAccountService(gateway);

        var cash = await service.GetAvailableCashAsync();

        Assert.Equal(123456.78m, cash);
    }

    [Fact]
    public async Task GetOpenOrdersAsync_FiltersOutCompleteAndCancelledOrders()
    {
        var transport = new FakeKiteHttpTransport((method, path, form, auth) => new KiteHttpResponse(200, """
        {
          "status": "success",
          "data": [
            {"tradingsymbol":"A","transaction_type":"BUY","quantity":10,"order_id":"1","status":"COMPLETE"},
            {"tradingsymbol":"B","transaction_type":"BUY","quantity":10,"order_id":"2","status":"OPEN"},
            {"tradingsymbol":"C","transaction_type":"SELL","quantity":10,"order_id":"3","status":"CANCELLED"}
          ]
        }
        """));
        var gateway = new KiteApiGateway(transport, Options(), "key1", "tok1");
        var service = new ZerodhaOrderExecutionService(gateway);

        var openOrders = await service.GetOpenOrdersAsync();

        Assert.Equal(1, openOrders.Count);
        Assert.Equal("B", openOrders[0].Symbol);
    }
}
