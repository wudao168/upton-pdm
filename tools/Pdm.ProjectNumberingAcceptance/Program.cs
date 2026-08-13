using System.Text.Json;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Infrastructure;

var baseConnection = Environment.GetEnvironmentVariable("PDM_ACCEPTANCE_CONNECTION")
    ?? throw new InvalidOperationException("PDM_ACCEPTANCE_CONNECTION未设置。");
var password = Environment.GetEnvironmentVariable("PDM_DB_PASSWORD")
    ?? throw new InvalidOperationException("PDM_DB_PASSWORD未设置。");
var connectionBuilder = new MySqlConnectionStringBuilder(baseConnection) { Password = password };
var repository = new MySqlPdmRepository(Options.Create(new PdmDatabaseOptions
{
    Provider = "MySql",
    ConnectionString = connectionBuilder.ConnectionString,
    RunMigrations = false
}), TimeProvider.System);
var organizationId = Guid.Parse("70000000-0000-0000-0000-000000000001");
var customer = (await repository.ListCustomersAsync(false, CancellationToken.None))
    .SingleOrDefault(item => string.Equals(item.Code, "C00465", StringComparison.OrdinalIgnoreCase))
    ?? throw new InvalidOperationException("验收客户C00465不存在。");

var parent = await repository.CreateNumberedProjectAsync(Command("主项目", 1), CancellationToken.None);
var firstChild = await repository.CreateSubprojectAsync(new(parent.Id, "子项目一", null, 2), CancellationToken.None);
var secondChild = await repository.CreateSubprojectAsync(new(parent.Id, "子项目二", null, 1), CancellationToken.None);
var concurrent = await Task.WhenAll(Enumerable.Range(1, 10).Select(index =>
    repository.CreateNumberedProjectAsync(Command($"并发项目{index}", 2), CancellationToken.None)));

Console.WriteLine(JsonSerializer.Serialize(new
{
    database = connectionBuilder.Database,
    parent = new { parent.Code, parent.DeviceModel, parent.SerialNumbers },
    firstChild = new { firstChild.Code, firstChild.DeviceModel, firstChild.SerialNumbers },
    secondChild = new { secondChild.Code, secondChild.DeviceModel, secondChild.SerialNumbers },
    concurrentProjectCodesUnique = concurrent.Select(item => item.Code).Distinct().Count() == concurrent.Length,
    concurrentCustomerSequencesUnique = concurrent.Select(item => item.CustomerProjectSequence).Distinct().Count() == concurrent.Length,
    concurrentSerialNumbersUnique = concurrent.SelectMany(item => item.SerialNumbers).Distinct().Count() == concurrent.Sum(item => item.Quantity)
}, new JsonSerializerOptions { WriteIndented = true }));

CreateNumberedProjectCommand Command(string name, int quantity) => new(
    organizationId,
    "P",
    2,
    customer.Id,
    name,
    null,
    new DateOnly(2026, 8, 13),
    quantity,
    "engineer",
    @"D:\PDM\Vault",
    @"D:\PDM\Release");
