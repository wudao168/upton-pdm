using System.Globalization;
using Dapper;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<string> ReserveBusinessNumberAsync(string prefix, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // 重复前缀也立即取得排他锁，避免并发会话随后升级行锁发生死锁。
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO business_number_sequence(number_prefix,current_sequence) VALUES(@Prefix,0) ON DUPLICATE KEY UPDATE current_sequence=business_number_sequence.current_sequence",
            new { Prefix = prefix }, transaction, cancellationToken: cancellationToken));
        var current = await connection.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT current_sequence FROM business_number_sequence WHERE number_prefix=@Prefix FOR UPDATE",
            new { Prefix = prefix }, transaction, cancellationToken: cancellationToken));
        var existing = await connection.QuerySingleAsync<int>(new CommandDefinition(
            """
            SELECT COALESCE(MAX(sequence_number),0) FROM (
                SELECT CAST(RIGHT(number,4) AS UNSIGNED) AS sequence_number FROM project_application
                WHERE LEFT(number,CHAR_LENGTH(@Prefix))=@Prefix AND CHAR_LENGTH(number)=CHAR_LENGTH(@Prefix)+4
                  AND RIGHT(number,4) REGEXP '^[0-9]{4}$'
                UNION ALL
                SELECT CAST(RIGHT(package_number,4) AS UNSIGNED) FROM release_package
                WHERE LEFT(package_number,CHAR_LENGTH(@Prefix))=@Prefix AND CHAR_LENGTH(package_number)=CHAR_LENGTH(@Prefix)+4
                  AND RIGHT(package_number,4) REGEXP '^[0-9]{4}$'
            ) existing_numbers
            """, new { Prefix = prefix }, transaction, cancellationToken: cancellationToken));
        current = Math.Max(current, existing);
        if (current >= 9999) throw new PdmRuleException($"编号 {prefix} 的4位流水号已用尽，请联系管理员处理。");
        var next = current + 1;
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE business_number_sequence SET current_sequence=@Next WHERE number_prefix=@Prefix",
            new { Prefix = prefix, Next = next }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return prefix + next.ToString("D4", CultureInfo.InvariantCulture);
    }
}
