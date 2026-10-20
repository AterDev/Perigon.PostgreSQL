using Perigon.PostgreSQL.Migrations;

namespace Perigon.PostgreSQL.NativeAotSmoke;

[Migration(typeof(SmokeDbContext), "0001_aot_registry")]
public sealed class SmokeMigration : Migration
{
    public override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("SELECT 1");

    public override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("SELECT 1");
}
