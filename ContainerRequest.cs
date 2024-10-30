namespace TP;

public class ContainerRequest
{
    public string ImageName { get; set; } = string.Empty;
    public Dictionary<string, string>? EnvVariables { get; set; }
}
