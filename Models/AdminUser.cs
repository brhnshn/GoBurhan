using System;

namespace GoBurhan.Models
{
    public class AdminUser
    {
        public Guid Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string PasswordSalt { get; set; } = string.Empty;
        public string AuthToken { get; set; } = string.Empty;
    }
}
