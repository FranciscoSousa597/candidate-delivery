using AvailabilityProcessor.Configuration;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace AvailabilityProcessor.Dependencies;

public static class DependencyRegistration
{
    public static IServiceCollection AddDependencyClients(this IServiceCollection services)
    {
        services.AddHttpClient<ProductContextClient>((sp, http) => http.BaseAddress = Endpoints(sp).ProductContextBaseUrl)
            .AddDependencyResilience(ProductContextClient.Name);

        services.AddHttpClient<IAvailabilityGateway, HttpAvailabilityGateway>((sp, http) => http.BaseAddress = Endpoints(sp).AvailabilityApiBaseUrl)
            .AddDependencyResilience(HttpAvailabilityGateway.Name);

        services.AddHttpClient<IEventPublisher, HttpEventPublisher>((sp, http) => http.BaseAddress = Endpoints(sp).EventSinkBaseUrl)
            .AddDependencyResilience(HttpEventPublisher.Name);

        return services;
    }

    private static DependencyOptions Endpoints(IServiceProvider services) =>
        services.GetRequiredService<IOptions<DependencyOptions>>().Value;

    private static void AddDependencyResilience(this IHttpClientBuilder builder, string pipelineName) =>
        builder.AddResilienceHandler(pipelineName, static (pipeline, context) =>
        {
            var options = context.ServiceProvider.GetRequiredService<IOptions<ResilienceOptions>>().Value;

            // Strategies run from outer to inner.
            // 1. Concurrency limiter: caps in-flight calls to this dependency across single events, bulk jobs and the
            //    outbox relay. A call keeps its permit while it backs off, so a burst of 429s slows callers down
            //    instead of piling more requests onto the dependency.
            pipeline.AddConcurrencyLimiter(options.MaxConcurrentRequests, options.MaxQueuedRequests);

            // 2. Upper bound for one logical call, retries included.
            pipeline.AddTimeout(options.TotalTimeout);

            // 3. Retries for transient failures only: 408, 429, 5xx, network errors and attempt timeouts.
            //    404 and the other 4xx responses are final. A Retry-After header overrides the exponential backoff.
            pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = options.MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = options.BaseRetryDelay,
                MaxDelay = options.MaxRetryDelay,
                ShouldRetryAfterHeader = true,
            });

            // 4. Circuit breaker: when most of the recent calls failed, the dependency is treated as down. Calls are
            //    then rejected without being sent, until a trial call succeeds. It sits inside the retry, so every
            //    attempt counts, and a rejected call is not retried: it fails at once.
            pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = options.CircuitBreaker.FailureRatio,
                MinimumThroughput = options.CircuitBreaker.MinimumThroughput,
                SamplingDuration = options.CircuitBreaker.SamplingDuration,
                BreakDuration = options.CircuitBreaker.BreakDuration,
            });

            // 5. Timeout for each individual attempt.
            pipeline.AddTimeout(options.AttemptTimeout);
        });
}
