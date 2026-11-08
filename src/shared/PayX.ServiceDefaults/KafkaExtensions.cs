using Confluent.Kafka;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PayX.ServiceDefaults;

/// <summary>Kafka clients configured once, from <c>Kafka:BootstrapServers</c>.</summary>
public static class KafkaExtensions
{
    /// <summary>
    /// One producer per process (it is thread-safe and batches internally),
    /// set up so that a confirmed message is a durable, single message:
    /// <list type="bullet">
    /// <item><c>Acks.All</c>: confirmed only once every in-sync replica has
    /// it (with MSK's 3 replicas and min.insync.replicas 2, a broker loss
    /// loses nothing);</item>
    /// <item><c>EnableIdempotence</c>: the broker drops the producer's own
    /// internal retries, so a network blip doesn't write a message twice or
    /// out of order;</item>
    /// <item><c>Murmur2Random</c>: the same key → partition mapping as Java
    /// clients (librdkafka's default differs), so a consumer in any language
    /// agrees on where a payment's events live.</item>
    /// </list>
    /// </summary>
    public static WebApplicationBuilder AddKafkaProducer(this WebApplicationBuilder builder)
    {
        var config = ProducerConfig(builder.Configuration);
        builder.Services.AddSingleton<IProducer<string, string>>(_ => new ProducerBuilder<string, string>(config).Build());
        return builder;
    }

    /// <summary>
    /// A consumer of <paramref name="topic"/> in group <paramref name="groupId"/>
    /// whose messages go to <typeparamref name="THandler"/>; messages it can't
    /// handle go to <paramref name="deadLetterTopic"/> (6.6). Registers the
    /// shared producer too (the dead-letter copy is produced with it).
    /// </summary>
    public static WebApplicationBuilder AddDeadLetteringConsumer<THandler>(this WebApplicationBuilder builder,
        string topic, string groupId, string deadLetterTopic)
        where THandler : class, Kafka.IMessageHandler
    {
        if (builder.Services.All(s => s.ServiceType != typeof(IProducer<string, string>)))
            builder.AddKafkaProducer();

        builder.Services.AddScoped<THandler>();
        builder.Services.AddSingleton(new Kafka.ConsumerSettings
        {
            Topic = topic,
            GroupId = groupId,
            DeadLetterTopic = deadLetterTopic,
            BootstrapServers = BootstrapServers(builder.Configuration),
            MaxAttempts = builder.Configuration.GetValue("Kafka:Consumer:MaxAttempts", 5),
            RetryDelay = builder.Configuration.GetValue("Kafka:Consumer:RetryDelay", TimeSpan.FromMilliseconds(200)),
        });
        builder.Services.AddHostedService<Kafka.DeadLetteringConsumer<THandler>>();
        return builder;
    }

    public static ProducerConfig ProducerConfig(IConfiguration configuration) => new()
    {
        BootstrapServers = BootstrapServers(configuration),
        Acks = Acks.All,
        EnableIdempotence = true,
        Partitioner = Partitioner.Murmur2Random,
        MessageTimeoutMs = 10_000,   // give up on a message after 10 s instead of the default 5 minutes
    };

    public static string BootstrapServers(IConfiguration configuration) =>
        configuration["Kafka:BootstrapServers"]
        ?? throw new InvalidOperationException("Missing configuration: Kafka:BootstrapServers");
}
