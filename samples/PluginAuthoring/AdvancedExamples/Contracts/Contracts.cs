namespace Guide.Contracts;

public interface IPriceService
{
    double Subtotal(double unitPrice, double count);
}

public sealed record QuoteRequest(double UnitPrice, double Count);
public sealed record QuoteReply(double Total);
public sealed record OrderPriced(double Total);
