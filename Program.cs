using System.Diagnostics;
using System.Runtime.InteropServices;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.AspNetCore.Mvc;
using TP;

const string ContainerNameStart = "container";
const string ContainerNameFormat = ContainerNameStart + "-{0}-{1}"; // {0} = imageName, {1} = GUID

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

string daemonUri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ?
    "npipe://./pipe/docker_engine" :
    "unix:///var/run/docker.sock";

var dockerClient = new DockerClientConfiguration(new Uri(daemonUri))
    .CreateClient();

// Endpoint POST pour lancer un conteneur avec variables d'environnement
app.MapPost("/container", async (HttpContext context, [FromBody] ContainerRequest containerRequest) =>
{
    try
    {
        var imageName = containerRequest.ImageName;

        // Vérifier et compléter l'image avec :latest si nécessaire
        if (!imageName.Contains(":"))
            imageName += ":latest";

        Console.WriteLine("Searching Image " + imageName);

        var images = await dockerClient.Images.ListImagesAsync(new ImagesListParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                { "reference", new Dictionary<string, bool> { { imageName, true } } }
            }
        });

        Stopwatch stopWatch;

        // Si l'image n'est pas trouvée localement, télécharger
        if (images.Count == 0)
        {
            Console.WriteLine("Image not found, downloading...");

            stopWatch = Stopwatch.StartNew();
            await dockerClient.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = imageName },
                null,
                new Progress<JSONMessage>(m => Console.WriteLine(m.Status))
            );
            stopWatch.Stop();

            Console.WriteLine($"Download done in {stopWatch.ElapsedMilliseconds} milliseconds.");
        }
        else
        {
            Console.WriteLine("Image found locally.");
        }

        var containerName = string.Format(ContainerNameFormat, imageName.Replace(":", "-"), Guid.NewGuid());

        // Configurer les variables d'environnement pour le conteneur
        List<string> envVariables = containerRequest.EnvVariables?
            .Select(kv => $"{kv.Key}={kv.Value}")
            .ToList() ?? new List<string>();

        // Créer le conteneur avec les variables d'environnement
        var createParams = new CreateContainerParameters
        {
            Image = imageName,
            Name = containerName,
            HostConfig = new HostConfig
            {
                AutoRemove = false
            },
            Env = envVariables
        };

        Console.WriteLine($"Creating container \"{containerName}\"...");
        stopWatch = Stopwatch.StartNew();

        var response = await dockerClient.Containers.CreateContainerAsync(createParams);

        stopWatch.Stop();
        Console.WriteLine($"Container created in {stopWatch.ElapsedMilliseconds} milliseconds with ID : \"{response.ID}\".");

        Console.WriteLine($"Starting container \"{containerName}\"...");
        stopWatch = Stopwatch.StartNew();

        await dockerClient.Containers.StartContainerAsync(response.ID, new ContainerStartParameters());

        stopWatch.Stop();
        Console.WriteLine($"Container started in {stopWatch.ElapsedMilliseconds} milliseconds.");

        var routeValues = new { containerNameOrId = response.ID };
        return Results.CreatedAtRoute("ContainerStatus", routeValues, new
        {
            message = "Container successfully created and started.",
            containerName = containerName,
            containerId = response.ID
        });
    }
    catch (DockerApiException ex)
    {
        context.Response.StatusCode = 500;
        return Results.Json(new { message = "Error", details = ex.Message });
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = 500;
        return Results.Json(new { message = "Error", details = ex.Message });
    }
})
.WithName("StartContainer")
.WithOpenApi();

// Endpoint GET pour obtenir le statut d'un conteneur
app.MapGet("/container/{containerNameOrId}", async ([FromRoute] string containerNameOrId) =>
{
    try
    {
        Console.WriteLine($"Searching container \"{containerNameOrId}\"...");
        var stopWatch = Stopwatch.StartNew();

        // Recherche du conteneur par nom
        var containers = await dockerClient.Containers.ListContainersAsync(new ContainersListParameters { All = true });
        var container = containers.FirstOrDefault(c => c.Names.Any(name => name.Equals("/" + containerNameOrId)) || c.ID == containerNameOrId);

        stopWatch.Stop();
        Console.WriteLine($"Search finished in {stopWatch.ElapsedMilliseconds} milliseconds.");

        if (container == null)
        {
            Console.WriteLine($"Container not found : \"{containerNameOrId}\"");
            return Results.NotFound(new { message = $"Container '{containerNameOrId}' not found." });
        }

        Console.WriteLine("Container found, returning status.");

        // Récupération du statut du conteneur
        return Results.Ok(new
        {
            containerName = container.Names,
            containerId = container.ID,
            state = container.State,
            status = container.Status,
            image = container.Image
        });
    }
    catch (DockerApiException ex)
    {
        return Results.Problem("Error : " + ex.Message);
    }
})
.WithName("ContainerStatus")
.WithOpenApi();

// Endpoint DELETE pour supprimer un conteneur
app.MapDelete("/container/{containerNameOrId}", async ([FromRoute] string containerNameOrId) =>
{
    try
    {
        Console.WriteLine($"Searching container \"{containerNameOrId}\"...");
        var stopWatch = Stopwatch.StartNew();

        // Recherche du conteneur par nom
        var containers = await dockerClient.Containers.ListContainersAsync(new ContainersListParameters { All = true });
        var container = containers.FirstOrDefault(c => c.Names.Any(name => name.Equals("/" + containerNameOrId) || c.ID == containerNameOrId));

        stopWatch.Stop();
        Console.WriteLine($"Search finished in {stopWatch.ElapsedMilliseconds} milliseconds.");

        if (container == null)
        {
            Console.WriteLine($"Container not found : \"{containerNameOrId}\"");
            return Results.NotFound(new { message = $"Container '{containerNameOrId}' not found." });
        }

        Console.WriteLine($"Deleting container \"{containerNameOrId}\"...");
        stopWatch = Stopwatch.StartNew();

        // Suppression du conteneur
        await dockerClient.Containers.RemoveContainerAsync(container.ID, new ContainerRemoveParameters { Force = true });

        stopWatch.Stop();
        Console.WriteLine($"Deletion finished in {stopWatch.ElapsedMilliseconds} milliseconds.");

        return Results.Ok(new { message = $"Container '{containerNameOrId}' successfully deleted." });
    }
    catch (DockerApiException ex)
    {
        return Results.Problem("Error : " + ex.Message);
    }
})
.WithName("DeleteContainer")
.WithOpenApi();

app.MapGet("/container", async (HttpContext context) =>
{
    try
    {
        // Récupérer la liste des conteneurs
        var containers = await dockerClient.Containers.ListContainersAsync(new ContainersListParameters()
        {
            All = true // Inclure les conteneurs arrêtés
        });

        // Filtrer les conteneurs dont le nom commence par ContainerNameStart
        var filteredContainers = containers
            .Where(c => c.Names.Any(n => n.StartsWith($"/{ContainerNameStart}")))
            .Select(c => new
            {
                c.ID,
                c.Image,
                c.Names,
                c.State,
                c.Status
            })
            .ToList();

        return Results.Ok(filteredContainers);
    }
    catch (DockerApiException ex)
    {
        context.Response.StatusCode = 500;
        return Results.Json(new { message = "Erreur lors de la récupération des conteneurs", details = ex.Message });
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = 500;
        return Results.Json(new { message = "Erreur interne", details = ex.Message });
    }
})
.WithName("ListContainers")
.WithOpenApi();

await app.RunAsync();
