using Microsoft.EntityFrameworkCore;

namespace Zeeq.Data;

/// <summary>
/// The EF Core `DbContext` for Zeeq.
/// </summary>
public abstract class ZeeqDbContextBase(DbContextOptions options) : DbContext(options)
{
    /// <summary>
    /// Configures the EF Core model by applying all configurations from the registered
    /// `IEntityConfigurationModule` instances.
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureProviderModel(modelBuilder);
    }

    /// <summary>
    /// Inheriting classes override and provide provider-specific model configuration in this method.
    /// This allows us to keep the base configuration consistent while enabling customization for
    /// different providers.
    /// </summary>
    /// <param name="modelBuilder">The model builder used to configure the EF Core model.</param>
    protected virtual void ConfigureProviderModel(ModelBuilder modelBuilder) { }
}
