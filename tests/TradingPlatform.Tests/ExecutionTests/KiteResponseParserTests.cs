using TradingPlatform.Execution.Zerodha;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.ExecutionTests;

public class KiteResponseParserTests
{
    [Fact]
    public void ParseOrderPlacement_Success_ReturnsOrderId()
    {
        const string json = """{"status":"success","data":{"order_id":"151220000000000"}}""";
        var orderId = KiteResponseParser.ParseOrderPlacement(200, json);
        Assert.Equal("151220000000000", orderId);
    }

    [Fact]
    public void ParseOrderPlacement_TokenException_ThrowsKiteTokenException()
    {
        const string json = """{"status":"error","error_type":"TokenException","message":"Invalid or expired access token."}""";
        Assert.Throws<KiteTokenException>(() => KiteResponseParser.ParseOrderPlacement(403, json));
    }

    [Fact]
    public void ParseOrderPlacement_OrderException_ThrowsKiteOrderException()
    {
        const string json = """{"status":"error","error_type":"OrderException","message":"Insufficient funds."}""";
        try
        {
            KiteResponseParser.ParseOrderPlacement(500, json);
            Assert.True(false, "Expected KiteOrderException");
        }
        catch (KiteOrderException ex)
        {
            Assert.Equal("Insufficient funds.", ex.Message);
        }
    }

    [Fact]
    public void ParseOrderPlacement_InputException_ThrowsKiteInputException()
    {
        const string json = """{"status":"error","error_type":"InputException","message":"Invalid tradingsymbol."}""";
        Assert.Throws<KiteInputException>(() => KiteResponseParser.ParseOrderPlacement(400, json));
    }

    [Fact]
    public void ParseOrderPlacement_UnrecognizedErrorType_ThrowsGeneralException()
    {
        const string json = """{"status":"error","error_type":"SomeNewExceptionType","message":"Unknown problem."}""";
        Assert.Throws<KiteGeneralException>(() => KiteResponseParser.ParseOrderPlacement(500, json));
    }

    [Fact]
    public void ParseOrderPlacement_MalformedJson_ThrowsKiteDataException()
    {
        Assert.Throws<KiteDataException>(() => KiteResponseParser.ParseOrderPlacement(200, "not json at all"));
    }

    [Fact]
    public void ThrowIfError_RateLimited429_ThrowsKiteNetworkException()
    {
        const string json = """{"message":"Too many requests"}""";
        try
        {
            KiteResponseParser.ThrowIfError(429, json);
            Assert.True(false, "Expected KiteNetworkException");
        }
        catch (KiteNetworkException ex)
        {
            Assert.Equal(429, ex.HttpStatusCode);
        }
    }

    [Fact]
    public void ThrowIfError_2xx_DoesNotThrow()
    {
        KiteResponseParser.ThrowIfError(200, """{"status":"success","data":{}}""");
        // no exception == pass
    }

    [Fact]
    public void ThrowIfError_UnparseableBody_502_ThrowsNetworkException_NotSwallowed()
    {
        try
        {
            KiteResponseParser.ThrowIfError(502, "<html>Bad Gateway</html>");
            Assert.True(false, "Expected KiteNetworkException");
        }
        catch (KiteNetworkException ex)
        {
            Assert.Equal(502, ex.HttpStatusCode);
        }
    }

    [Fact]
    public void ParseSessionToken_Success_ReturnsAccessToken()
    {
        const string json = """{"status":"success","data":{"access_token":"abc123token","user_id":"AB1234"}}""";
        var token = KiteResponseParser.ParseSessionToken(200, json);
        Assert.Equal("abc123token", token);
    }

    [Fact]
    public void ParsePositions_ReturnsOnlyNonZeroQuantityPositions()
    {
        const string json = """
        {
          "status": "success",
          "data": {
            "net": [
              {"tradingsymbol": "SBIN", "exchange": "NSE", "quantity": 100, "average_price": 560.50},
              {"tradingsymbol": "TCS", "exchange": "NSE", "quantity": 0, "average_price": 0}
            ],
            "day": []
          }
        }
        """;

        var positions = KiteResponseParser.ParsePositions(200, json);

        Assert.Equal(1, positions.Count);
        Assert.Equal("SBIN", positions[0].Symbol);
        Assert.Equal(100m, positions[0].Quantity);
        Assert.Equal(560.50m, positions[0].AverageEntryPrice);
    }

    [Fact]
    public void ParsePositions_MissingNetArray_ThrowsKiteDataException()
    {
        const string json = """{"status":"success","data":{"day":[]}}""";
        Assert.Throws<KiteDataException>(() => KiteResponseParser.ParsePositions(200, json));
    }

    [Fact]
    public void ParseAvailableCash_NestedEquityShape_ReturnsCash()
    {
        const string json = """
        {
          "status": "success",
          "data": {
            "equity": {
              "enabled": true,
              "net": 100000.50,
              "available": {"cash": 95000.25, "live_balance": 100000.50}
            }
          }
        }
        """;

        var cash = KiteResponseParser.ParseAvailableCash(200, json);
        Assert.Equal(95000.25m, cash);
    }

    [Fact]
    public void ParseAvailableCash_SegmentDirectShape_ReturnsCash()
    {
        // /user/margins/equity returns the segment object directly, without the outer "equity" wrapper.
        const string json = """{"status":"success","data":{"enabled":true,"net":50000.00,"available":{"cash":48000.00}}}""";
        var cash = KiteResponseParser.ParseAvailableCash(200, json);
        Assert.Equal(48000.00m, cash);
    }

    [Fact]
    public void ParseOrders_MapsCompleteStatus_ToFilled()
    {
        const string json = """
        {
          "status": "success",
          "data": [
            {"tradingsymbol": "SBIN", "transaction_type": "BUY", "order_type": "MARKET", "quantity": 50, "order_id": "abc123", "status": "COMPLETE", "tag": "corr-1"}
          ]
        }
        """;

        var orders = KiteResponseParser.ParseOrders(200, json);

        Assert.Equal(1, orders.Count);
        Assert.Equal(TradingPlatform.Core.Enums.OrderState.Filled, orders[0].State);
        Assert.Equal("abc123", orders[0].BrokerOrderId);
    }

    [Fact]
    public void ParseOrders_UnrecognizedStatus_MapsToUnknown_NeverGuessed()
    {
        const string json = """
        {"status":"success","data":[{"tradingsymbol":"SBIN","transaction_type":"BUY","quantity":50,"order_id":"x","status":"SOME NEW STATUS"}]}
        """;

        var orders = KiteResponseParser.ParseOrders(200, json);
        Assert.Equal(TradingPlatform.Core.Enums.OrderState.Unknown, orders[0].State);
    }
}
