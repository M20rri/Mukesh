﻿using Finbuckle.MultiTenant;
using Mapster;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using ICISAdminPortal.Application.Common.Exceptions;
using ICISAdminPortal.Application.Common.Persistence;
using ICISAdminPortal.Application.Multitenancy;
using ICISAdminPortal.Infrastructure.Persistence;
using ICISAdminPortal.Infrastructure.Persistence.Initialization;
using System.Net;

namespace ICISAdminPortal.Infrastructure.Multitenancy;
internal class TenantService : ITenantService
{
    private readonly IMultiTenantStore<FSHTenantInfo> _tenantStore;
    private readonly IConnectionStringSecurer _csSecurer;
    private readonly IDatabaseInitializer _dbInitializer;
    private readonly IStringLocalizer _t;
    private readonly DatabaseSettings _dbSettings;

    public TenantService(
        IMultiTenantStore<FSHTenantInfo> tenantStore,
        IConnectionStringSecurer csSecurer,
        IDatabaseInitializer dbInitializer,
        IStringLocalizer<TenantService> localizer,
        IOptions<DatabaseSettings> dbSettings)
    {
        _tenantStore = tenantStore;
        _csSecurer = csSecurer;
        _dbInitializer = dbInitializer;
        _t = localizer;
        _dbSettings = dbSettings.Value;
    }

    public async Task<List<TenantDto>> GetAllAsync()
    {
        var tenants = (await _tenantStore.GetAllAsync()).Adapt<List<TenantDto>>();
        tenants.ForEach(t => t.ConnectionString = _csSecurer.MakeSecure(t.ConnectionString));
        return tenants;
    }

    public async Task<bool> ExistsWithIdAsync(string id) =>
        await _tenantStore.TryGetAsync(id) is not null;

    public async Task<bool> ExistsWithNameAsync(string name) =>
        (await _tenantStore.GetAllAsync()).Any(t => t.Name == name);

    public async Task<TenantDto> GetByIdAsync(string id) =>
        (await GetTenantInfoAsync(id))
            .Adapt<TenantDto>();

    public async Task<string> CreateAsync(CreateTenantRequest request, CancellationToken cancellationToken)
    {
        if (request.ConnectionString?.Trim() == _dbSettings.ConnectionString.Trim()) request.ConnectionString = string.Empty;

        var tenant = new FSHTenantInfo(request.Id, request.Name, request.ConnectionString, request.AdminEmail, request.Issuer);
        await _tenantStore.TryAddAsync(tenant);

        // TODO: run this in a hangfire job? will then have to send mail when it's ready or not
        try
        {
            await _dbInitializer.InitializeApplicationDbForTenantAsync(tenant, cancellationToken);
        }
        catch
        {
            await _tenantStore.TryRemoveAsync(request.Id);
            throw;
        }

        return tenant.Id;
    }

    public async Task<string> ActivateAsync(string id)
    {
        var tenant = await GetTenantInfoAsync(id);

        if (tenant.IsActive)
        {
            throw new Application.Exceptions.ValidationException(_t["Tenant is already Activated."], (int)HttpStatusCode.BadRequest);
        }

        tenant.Activate();

        await _tenantStore.TryUpdateAsync(tenant);

        return _t["Tenant {0} is now Activated.", id];
    }

    public async Task<string> DeactivateAsync(string id)
    {
        var tenant = await GetTenantInfoAsync(id);
        if (!tenant.IsActive)
        {
            throw new Application.Exceptions.ValidationException(_t["Tenant is already Deactivated."], (int)HttpStatusCode.BadRequest);
        }

        tenant.Deactivate();
        await _tenantStore.TryUpdateAsync(tenant);
        return _t["Tenant {0} is now Deactivated.", id];
    }

    public async Task<string> UpdateSubscription(string id, DateTime extendedExpiryDate)
    {
        var tenant = await GetTenantInfoAsync(id);
        tenant.SetValidity(extendedExpiryDate);
        await _tenantStore.TryUpdateAsync(tenant);
        return _t["Tenant {0}'s Subscription Upgraded. Now Valid till {1}.", id, tenant.ValidUpto];
    }

    private async Task<FSHTenantInfo> GetTenantInfoAsync(string id) =>
        await _tenantStore.TryGetAsync(id)
            ?? throw new Application.Exceptions.ValidationException(_t["{0} {1} Not Found.", typeof(FSHTenantInfo).Name, id], (int)HttpStatusCode.BadRequest);
}