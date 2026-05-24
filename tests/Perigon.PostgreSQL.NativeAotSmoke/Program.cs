using Perigon.PostgreSQL;
using Perigon.PostgreSQL.NativeAotSmoke;

var db = new SmokeDbContext();
var sql = db.Users
    .Where(u => u.Age > 18 && u.Tags!.Contains("aot"))
    .OrderBy(u => u.UserName)
    .Take(5)
    .ToQuerySql();

Console.WriteLine(sql.CommandText);
Console.WriteLine(sql.Parameters.Count);
