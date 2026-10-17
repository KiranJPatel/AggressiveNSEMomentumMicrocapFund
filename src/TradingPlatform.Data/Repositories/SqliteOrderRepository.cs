using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Sqlite;

namespace TradingPlatform.Data.Repositories;

/// <summary>Orders + OrderEvents + Fills (spec §6.3, §7.2) — kept together since they share one lifecycle; see IOrderRepository's remarks.</summary>
public sealed class SqliteOrderRepository : IOrderRepository
{
    // Terminal states: an order in one of these will never transition again,
    // so it's excluded from GetOpenOrdersAsync (spec §6.3's state machine).
    private static readonly OrderState[] TerminalStates =
        { OrderState.Filled, OrderState.Cancelled, OrderState.Rejected, OrderState.Failed };

    private readonly SqliteConnection _connection;

    public SqliteOrderRepository(SqliteConnection connection) => _connection = connection;

    public Task<long> InsertAsync(Order order, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var id = _connection.ExecuteInsert(
            """
            INSERT INTO Orders
                (SignalId, Symbol, Side, Type, State, Quantity, LimitPrice, StopPrice,
                 BrokerOrderId, RejectionReason, CorrelationId, CreatedAt, UpdatedAt)
            VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?);
            """,
            order.SignalId, order.Symbol, order.Side, order.Type, order.State, order.Quantity,
            order.LimitPrice, order.StopPrice, order.BrokerOrderId, order.RejectionReason,
            order.CorrelationId, now, now);
        return Task.FromResult(id);
    }

    public Task RecordStateTransitionAsync(long orderId, OrderState fromState, OrderState toState, string? brokerOrderId,
        string? rejectionReason, string? detail, DateTimeOffset timestamp, CancellationToken cancellationToken = default)
    {
        using var tx = _connection.BeginTransaction();

        // COALESCE keeps the existing value when the caller passes null for
        // Broker Order Id/RejectionReason (e.g. a plain Acknowledged->Open
        // transition carries neither) rather than clobbering a value a prior
        // transition already recorded.
        _connection.Execute(
            """
            UPDATE Orders SET State = ?, BrokerOrderId = COALESCE(?, BrokerOrderId),
                RejectionReason = COALESCE(?, RejectionReason), UpdatedAt = ?
            WHERE Id = ?;
            """,
            toState, brokerOrderId, rejectionReason, timestamp, orderId);

        _connection.ExecuteInsert(
            "INSERT INTO OrderEvents (OrderId, FromState, ToState, Detail, Timestamp) VALUES (?,?,?,?,?);",
            orderId, fromState, toState, detail, timestamp);

        tx.Commit();
        return Task.CompletedTask;
    }

    public Task<long> RecordFillAsync(Fill fill, CancellationToken cancellationToken = default)
    {
        var id = _connection.ExecuteInsert(
            """
            INSERT INTO Fills (OrderId, Symbol, Quantity, Price, SlippageAmount, Commission, FilledAt)
            VALUES (?,?,?,?,?,?,?);
            """,
            fill.OrderId, fill.Symbol, fill.Quantity, fill.Price, fill.SlippageAmount, fill.Commission, fill.FilledAt);
        return Task.FromResult(id);
    }

    public Task<Order?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        var result = _connection.QuerySingleOrDefault("SELECT * FROM Orders WHERE Id = ?;", MapOrder, id);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Order>> GetRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        var result = _connection.Query("SELECT * FROM Orders ORDER BY CreatedAt DESC LIMIT ?;", MapOrder, limit);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Order>> GetOpenOrdersAsync(CancellationToken cancellationToken = default)
    {
        var placeholders = string.Join(",", TerminalStates.Select(_ => "?"));
        var result = _connection.Query(
            $"SELECT * FROM Orders WHERE State NOT IN ({placeholders}) ORDER BY CreatedAt;",
            MapOrder, TerminalStates.Cast<object?>().ToArray());
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<OrderEvent>> GetEventsAsync(long orderId, CancellationToken cancellationToken = default)
    {
        var result = _connection.Query(
            "SELECT * FROM OrderEvents WHERE OrderId = ? ORDER BY Timestamp;", MapEvent, orderId);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Fill>> GetFillsAsync(long orderId, CancellationToken cancellationToken = default)
    {
        var result = _connection.Query(
            "SELECT * FROM Fills WHERE OrderId = ? ORDER BY FilledAt;", MapFill, orderId);
        return Task.FromResult(result);
    }

    private static Order MapOrder(SqliteRow row) => new()
    {
        Id = row.GetInt64("Id"),
        SignalId = row.GetNullableInt64("SignalId"),
        Symbol = row.GetString("Symbol"),
        Side = row.GetEnum<OrderSide>("Side"),
        Type = row.GetEnum<OrderType>("Type"),
        State = row.GetEnum<OrderState>("State"),
        Quantity = row.GetDecimal("Quantity"),
        LimitPrice = row.GetNullableDecimal("LimitPrice"),
        StopPrice = row.GetNullableDecimal("StopPrice"),
        BrokerOrderId = row.GetNullableString("BrokerOrderId"),
        RejectionReason = row.GetNullableString("RejectionReason"),
        CorrelationId = row.GetString("CorrelationId"),
        CreatedAt = row.GetDateTimeOffset("CreatedAt"),
        UpdatedAt = row.GetDateTimeOffset("UpdatedAt"),
    };

    private static OrderEvent MapEvent(SqliteRow row) => new()
    {
        Id = row.GetInt64("Id"),
        OrderId = row.GetInt64("OrderId"),
        FromState = row.GetEnum<OrderState>("FromState"),
        ToState = row.GetEnum<OrderState>("ToState"),
        Detail = row.GetNullableString("Detail"),
        Timestamp = row.GetDateTimeOffset("Timestamp"),
    };

    private static Fill MapFill(SqliteRow row) => new()
    {
        Id = row.GetInt64("Id"),
        OrderId = row.GetInt64("OrderId"),
        Symbol = row.GetString("Symbol"),
        Quantity = row.GetDecimal("Quantity"),
        Price = row.GetDecimal("Price"),
        SlippageAmount = row.GetDecimal("SlippageAmount"),
        Commission = row.GetDecimal("Commission"),
        FilledAt = row.GetDateTimeOffset("FilledAt"),
    };
}
