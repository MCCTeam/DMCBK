using System;
using System.Globalization;
using System.Runtime.Loader;
using System.Threading.Tasks;
using DMCBK.PluginSdk;
using Guide.Contracts;

public sealed class PriceConsumer : IPlugin
{
    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "guide-order";
        descriptor.Version = "1.0.0";
    }

    public Task ActivateAsync(PluginContext context)
    {
        context.Messenger.Subscribe<OrderPriced>(message =>
            context.Variables.Set("guide_order_notification", Format(message.Total)));
        if (!context.Services.TryGet<IPriceService>(out var service))
            throw new InvalidOperationException(context.Strings.Get("missing_service"));

        context.Variables.Set("guide_order_service", Format(service.Subtotal(3, 4)));
        if (!context.Messenger.TryRequest<QuoteRequest, QuoteReply>(
                new QuoteRequest(3, 4), out var reply) || reply is null)
            throw new InvalidOperationException(context.Strings.Get("missing_responder"));
        context.Variables.Set("guide_order_response", Format(reply.Total));
        // This assembly must belong to the provider's collectible context, not the host.
        var contractContext = AssemblyLoadContext.GetLoadContext(typeof(IPriceService).Assembly);
        context.Variables.Set("guide_order_exported_context",
            contractContext is { IsCollectible: true } ? "yes" : "no");
        return Task.CompletedTask;
    }

    private static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);
}
