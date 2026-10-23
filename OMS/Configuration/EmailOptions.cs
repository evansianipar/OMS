namespace OMS.Configuration;

public sealed class EmailOptions
{
    public string Host { get; set; } = "smtp.gmail.com";
    public int Port { get; set; } = 587;
    public string Username { get; set; } = "outingmanagementsystem2026@gmail.com";
    public string Password { get; set; } = string.Empty;
    public string SenderName { get; set; } = "Outing Management System";
}
