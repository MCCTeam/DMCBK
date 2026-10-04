using System.Threading.Tasks;
using DMCBK.PluginSdk;
using Guide.Contracts;

public sealed class PriceProvider : IPlugin
{
    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "guide-pricing";
        descriptor.Version = "1.0.0";
    }

    public Task ActivateAsync(PluginContext context)
    {
        context.Services.Register<IPriceService>(new PriceService());
        context.Messenger.RegisterResponder<QuoteRequest, QuoteReply>(request =>
        {
            double total = request.UnitPrice * request.Count;
            context.Messenger.Publish(new OrderPriced(total));
            return new QuoteReply(total);
        });
        return Task.CompletedTask;
    }

    private sealed class PriceService : IPriceService
    {
        public double Subtotal(double unitPrice, double count) => unitPrice * count;
    }
}
