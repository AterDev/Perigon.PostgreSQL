using Microsoft.EntityFrameworkCore;
using Perigon.PostgreSQL.Attributes;

namespace Perigon.PostgreSQL.Tests.Models;

[Microsoft.EntityFrameworkCore.Index(nameof(Email), Name = "uq_indexed_users_email", IsUnique = true)]
public sealed class IndexedUser
{
    public int Id { get; set; }

    public string Email { get; set; } = "";
}

[View("active_user_view", Schema = "reporting")]
public sealed class ActiveUserView
{
    public int Id { get; set; }

    public string UserName { get; set; } = "";
}