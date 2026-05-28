using Perigon.PostgreSQL;
using Perigon.PostgreSQL.AspNetCoreSample.Contracts;
using Perigon.PostgreSQL.AspNetCoreSample.Data;
using Perigon.PostgreSQL.AspNetCoreSample.Models;
using Perigon.PostgreSQL.RawSql;
using Perigon.PostgreSQL.Update;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Database=perigon_sample;Username=postgres;Password=postgres";
builder.Services.AddDbContext<SampleDbContext>(options => options.UseNpgsql(connectionString));

var app = builder.Build();
await SampleDatabase.InitializeAsync(connectionString);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/", () => Results.Ok(new
{
    Name = "Perigon.PostgreSQL ASP.NET Core sample",
    Endpoints = new[]
    {
        "POST /seed",
        "GET /users?status=active&tag=postgres&minAge=18",
        "GET /users/summaries",
        "GET /users/names",
        "GET /users/statuses",
        "GET /users/jsonpath?path=$.level ? (@ > 2)",
        "GET /users/with-blogs?status=active&publicOnly=true",
        "POST /users",
        "POST /users/bulk",
        "POST /users/bulk/values",
        "POST /users/upsert",
        "PATCH /users/{id}/status",
        "PATCH /users/{id}/age/increment",
        "DELETE /users/{id}",
        "GET /users/{id}/blogs",
        "POST /blogs",
        "POST /posts",
        "GET /reports/user-blog-links",
        "GET /reports/user-blogs",
        "GET /stats/users-by-status",
        "GET /stats/distinct-active-by-status",
        "GET /sql-preview/join",
        "GET /sql-preview/left-join",
        "GET /sql-preview/group-by"
    }
}));

app.MapPost("/seed", async (SampleDbContext db) =>
{
    var users = new[]
    {
        new SampleUser
        {
            UserName = "alice",
            Age = 31,
            IsActive = true,
            Status = "active",
            CreatedAt = DateTime.UtcNow,
            Tags = ["developer", "postgres"],
            ProfileJson = """{"level":3,"team":"platform"}"""
        },
        new SampleUser
        {
            UserName = "bob",
            Age = 42,
            IsActive = true,
            Status = "active",
            CreatedAt = DateTime.UtcNow,
            Tags = ["ops", "postgres"],
            ProfileJson = """{"level":2,"team":"infra"}"""
        },
        new SampleUser
        {
            UserName = "carol",
            Age = 27,
            IsActive = false,
            Status = "paused",
            CreatedAt = DateTime.UtcNow,
            Tags = ["analytics"],
            ProfileJson = """{"level":1,"team":"data"}"""
        }
    };

    await db.Users.UpsertManyAsync(users, u => u.UserName);
    var seededUsers = await db.Users.Where(u => u.UserName == "alice" || u.UserName == "bob" || u.UserName == "carol").ToListAsync();
    var alice = seededUsers.Single(u => u.UserName == "alice");
    var bob = seededUsers.Single(u => u.UserName == "bob");

    var blogs = new[]
    {
        await db.Blogs.InsertAsync(new SampleBlog { SampleUserId = alice.Id, Name = "PostgreSQL AOT", IsPublic = true, CreatedAt = DateTime.UtcNow }),
        await db.Blogs.InsertAsync(new SampleBlog { SampleUserId = bob.Id, Name = "Operational Notes", IsPublic = true, CreatedAt = DateTime.UtcNow })
    };

    _ = await db.Posts.InsertAsync(new SamplePost { SampleBlogId = blogs[0].Id, Title = "LINQ translation", Published = true, CreatedAt = DateTime.UtcNow });
    _ = await db.Posts.InsertAsync(new SamplePost { SampleBlogId = blogs[0].Id, Title = "COPY bulk insert", Published = true, CreatedAt = DateTime.UtcNow });
    _ = await db.Posts.InsertAsync(new SamplePost { SampleBlogId = blogs[1].Id, Title = "Monitoring", Published = false, CreatedAt = DateTime.UtcNow });

    return Results.Ok(new SeedResponse(seededUsers.Count, blogs.Length, 3));
});

app.MapGet("/users", async (SampleDbContext db, string? status, string? tag, int? minAge, bool? active) =>
{
    IQueryable<SampleUser> query = db.Users;
    if (!string.IsNullOrWhiteSpace(status))
    {
        query = query.Where(u => u.Status == status);
    }

    if (!string.IsNullOrWhiteSpace(tag))
    {
        query = query.Where(u => u.Tags!.Contains(tag));
    }

    if (minAge is not null)
    {
        query = query.Where(u => u.Age >= minAge.Value);
    }

    if (active is not null)
    {
        query = query.Where(u => u.IsActive == active.Value);
    }

    return Results.Ok(await query.OrderBy(u => u.UserName).ToListAsync());
});

app.MapGet("/users/summaries", async (SampleDbContext db, string? status) =>
{
    IQueryable<SampleUser> query = db.Users;
    if (!string.IsNullOrWhiteSpace(status))
    {
        query = query.Where(u => u.Status == status);
    }

    var summaries = await query
        .OrderBy(u => u.UserName)
        .Select(u => new UserSummary { Id = u.Id, UserName = u.UserName, Age = u.Age, Status = u.Status })
        .ToListAsync();
    return Results.Ok(summaries);
});

app.MapGet("/users/names", async (SampleDbContext db, string? status) =>
{
    IQueryable<SampleUser> query = db.Users;
    if (!string.IsNullOrWhiteSpace(status))
    {
        query = query.Where(u => u.Status == status);
    }

    var names = await query
        .OrderBy(u => u.UserName)
        .Select(u => u.UserName)
        .ToScalarListAsync();
    return Results.Ok(names);
});

app.MapGet("/users/statuses", async (SampleDbContext db) =>
{
    var statuses = await db.Users
        .Select(u => u.Status)
        .Distinct()
        .ToScalarListAsync();
    return Results.Ok(statuses);
});

app.MapGet("/users/jsonpath", async (SampleDbContext db, string path) =>
{
    var users = await db.Users
        .Where(u => u.ProfileJson.JsonbPathExists(path))
        .OrderBy(u => u.UserName)
        .ToListAsync();
    return Results.Ok(users);
});

app.MapGet("/users/with-blogs", async (SampleDbContext db, string? status, bool publicOnly) =>
{
    IQueryable<SampleUser> query = db.Users;
    if (!string.IsNullOrWhiteSpace(status))
    {
        query = query.Where(u => u.Status == status);
    }

    var graph = await query
        .OrderBy(u => u.UserName)
        .IncludeManyAsync(
            db.Blogs,
            u => u.Id,
            b => b.SampleUserId,
            blogs => publicOnly ? blogs.Where(b => b.IsPublic).OrderBy(b => b.Name) : blogs.OrderBy(b => b.Name),
            (u, blogs) => new UserWithBlogs(u, blogs));
    return Results.Ok(graph);
});

app.MapPost("/users", async (SampleDbContext db, CreateUserRequest request) =>
{
    var inserted = await db.Users.InsertAsync(new SampleUser
    {
        UserName = request.UserName,
        Age = request.Age,
        IsActive = request.IsActive,
        Status = request.Status,
        CreatedAt = DateTime.UtcNow,
        Tags = request.Tags,
        ProfileJson = request.ProfileJson
    });

    return Results.Created($"/users/{inserted.Id}", inserted);
});

app.MapPost("/users/bulk", async (SampleDbContext db, CreateUserRequest[] requests) =>
{
    var users = requests.Select(r => new SampleUser
    {
        UserName = r.UserName,
        Age = r.Age,
        IsActive = r.IsActive,
        Status = r.Status,
        CreatedAt = DateTime.UtcNow,
        Tags = r.Tags,
        ProfileJson = r.ProfileJson
    }).ToArray();

    await db.Users.BulkInsertAsync(users);
    return Results.Accepted(value: new { Inserted = users.Length });
});

app.MapPost("/users/bulk/values", async (SampleDbContext db, CreateUserRequest[] requests) =>
{
    var users = requests.Select(r => new SampleUser
    {
        UserName = r.UserName,
        Age = r.Age,
        IsActive = r.IsActive,
        Status = r.Status,
        CreatedAt = DateTime.UtcNow,
        Tags = r.Tags,
        ProfileJson = r.ProfileJson
    }).ToArray();

    await db.Users.BulkInsertAsync(
        users,
        new Perigon.PostgreSQL.Bulk.BulkInsertOptions
        {
            Mode = Perigon.PostgreSQL.Bulk.BulkInsertMode.InsertValues,
            BatchSize = 500
        });
    return Results.Accepted(value: new { Inserted = users.Length, Mode = "InsertValues" });
});

app.MapPost("/users/upsert", async (SampleDbContext db, CreateUserRequest[] requests) =>
{
    var users = requests.Select(r => new SampleUser
    {
        UserName = r.UserName,
        Age = r.Age,
        IsActive = r.IsActive,
        Status = r.Status,
        CreatedAt = DateTime.UtcNow,
        Tags = r.Tags,
        ProfileJson = r.ProfileJson
    }).ToArray();

    var affected = await db.Users.UpsertManyAsync(users, u => u.UserName);
    return Results.Ok(new { Affected = affected });
});

app.MapPatch("/users/{id:int}/status", async (SampleDbContext db, int id, UpdateStatusRequest request) =>
{
    var affected = await db.Users
        .Where(u => u.Id == id)
        .ExecuteUpdateAsync(s => s.Set(u => u.Status, request.Status));
    return affected == 0 ? Results.NotFound() : Results.Ok(new { Affected = affected });
});

app.MapPatch("/users/{id:int}/age/increment", async (SampleDbContext db, int id) =>
{
    var affected = await db.Users
        .Where(u => u.Id == id)
        .ExecuteUpdateAsync(s => s.SetExpression(u => u.Age, u => u.Age + 1));
    return affected == 0 ? Results.NotFound() : Results.Ok(new { Affected = affected });
});

app.MapDelete("/users/{id:int}", async (SampleDbContext db, int id) =>
{
    var affected = await db.Users.Where(u => u.Id == id).ExecuteDeleteAsync();
    return affected == 0 ? Results.NotFound() : Results.NoContent();
});

app.MapGet("/users/{id:int}/blogs", async (SampleDbContext db, int id, bool publicOnly) =>
{
    IQueryable<SampleBlog> query = db.Blogs.Where(b => b.SampleUserId == id);
    if (publicOnly)
    {
        query = query.Where(b => b.IsPublic);
    }

    return Results.Ok(await query.OrderBy(b => b.Name).ToListAsync());
});

app.MapPost("/blogs", async (SampleDbContext db, CreateBlogRequest request) =>
{
    var blog = await db.Blogs.InsertAsync(new SampleBlog
    {
        SampleUserId = request.UserId,
        Name = request.Name,
        IsPublic = request.IsPublic,
        CreatedAt = DateTime.UtcNow
    });
    return Results.Created($"/blogs/{blog.Id}", blog);
});

app.MapPost("/posts", async (SampleDbContext db, CreatePostRequest request) =>
{
    var post = await db.Posts.InsertAsync(new SamplePost
    {
        SampleBlogId = request.BlogId,
        Title = request.Title,
        Published = request.Published,
        CreatedAt = DateTime.UtcNow
    });
    return Results.Created($"/posts/{post.Id}", post);
});

app.MapGet("/reports/user-blogs", async (SampleDbContext db, string? userName) =>
{
    var rows = await db.SqlQuery<UserBlogRow>($"""
        select u.id as user_id,
               u.user_name,
               b.id as blog_id,
               b.name as blog_name,
               count(p.id) as post_count
        from sample_users u
        join sample_blogs b on b.sample_user_id = u.id
        left join sample_posts p on p.sample_blog_id = b.id
        where ({userName} is null or u.user_name = {userName})
        group by u.id, u.user_name, b.id, b.name
        order by u.user_name, b.name
        """).ToListAsync();

    return Results.Ok(rows);
});

app.MapGet("/reports/user-blog-links", async (SampleDbContext db, string? userName, bool publicOnly) =>
{
    IQueryable<SampleUser> users = db.Users;
    if (!string.IsNullOrWhiteSpace(userName))
    {
        users = users.Where(u => u.UserName == userName);
    }

    IQueryable<SampleBlog> blogs = db.Blogs;
    if (publicOnly)
    {
        blogs = blogs.Where(b => b.IsPublic);
    }

    var rows = await users
        .Join(
            blogs,
            u => u.Id,
            b => b.SampleUserId,
            (u, b) => new UserBlogLink
            {
                UserId = u.Id,
                UserName = u.UserName,
                BlogId = b.Id,
                BlogName = b.Name
            })
        .ToListAsync();
    return Results.Ok(rows);
});

app.MapGet("/stats/users-by-status", async (SampleDbContext db) =>
{
    var activeCount = await db.Users.Where(u => u.IsActive).LongCountAsync();
    var rows = await db.Users
        .GroupBy(u => new { u.Status, u.IsActive })
        .Select(g => new UserStatusStat
        {
            Status = g.Key.Status,
            IsActive = g.Key.IsActive,
            Count = g.LongCount(),
            AverageAge = g.Average(u => u.Age)
        })
        .OrderByDescending(x => x.Count)
        .ThenBy(x => x.Status)
        .ToListAsync();
    return Results.Ok(new { ActiveCount = activeCount, Rows = rows });
});

app.MapGet("/stats/distinct-active-by-status", async (SampleDbContext db) =>
{
    var rows = await db.Users
        .GroupBy(u => u.Status)
        .Select(g => new UserDistinctActiveStat
        {
            Status = g.Key,
            DistinctActiveStates = g.LongCountDistinct(u => u.IsActive)
        })
        .ToListAsync();
    return Results.Ok(rows);
});

app.MapGet("/sql-preview/join", (SampleDbContext db) =>
{
    var sql = db.Users
        .Join(db.Blogs, u => u.Id, b => b.SampleUserId, (u, b) => new { u.UserName, BlogName = b.Name })
        .ToSql();
    return Results.Ok(new { sql.CommandText, Parameters = sql.Parameters.Count });
});

app.MapGet("/sql-preview/left-join", (SampleDbContext db) =>
{
    var sql = db.Users
        .GroupJoin(db.Blogs, u => u.Id, b => b.SampleUserId, (u, blogs) => new { u, blogs })
        .SelectMany(x => x.blogs.DefaultIfEmpty(), (x, b) => new { x.u.UserName, BlogName = b!.Name })
        .ToSql();
    return Results.Ok(new { sql.CommandText, Parameters = sql.Parameters.Count });
});

app.MapGet("/sql-preview/group-by", (SampleDbContext db) =>
{
    var sql = db.Users
        .GroupBy(u => u.Status)
        .Select(g => new { Status = g.Key, Count = g.Count(), AverageAge = g.Average(u => u.Age) })
        .Where(x => x.Count > 0)
        .OrderByDescending(x => x.Count)
        .ToSql();
    return Results.Ok(new { sql.CommandText, Parameters = sql.Parameters.Count });
});

app.Run();
