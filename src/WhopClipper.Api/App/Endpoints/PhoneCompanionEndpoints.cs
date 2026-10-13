using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QRCoder;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.App.Endpoints;

public static class PhoneCompanionEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var phone = api.MapGroup("/publishing/phone-app").WithTags("Phone app");
        phone.MapGet("", async (PhoneCompanionService service, CancellationToken ct) => Results.Ok(await service.StatusAsync(ct)));
        phone.MapPost("/configure", async (HttpContext context, LocalPublishingRequestGuard guard, PhoneCompanionService service, CancellationToken ct) =>
        {
            if (!guard.IsValid(context)) return Results.StatusCode(403);
            if (!context.Request.HasFormContentType || context.Request.ContentLength is > PhoneCompanionService.MaxConfigBytes * 3)
                return Results.BadRequest(new { title = "Select the two Firebase JSON files." });
            try
            {
                var form = await context.Request.ReadFormAsync(ct);
                var serviceFile = form.Files.GetFile("serviceAccount"); var androidFile = form.Files.GetFile("androidSettings");
                if (serviceFile is null || androidFile is null || serviceFile.Length > PhoneCompanionService.MaxConfigBytes || androidFile.Length > PhoneCompanionService.MaxConfigBytes)
                    return Results.BadRequest(new { title = "Select a service-account key and google-services.json, each under 64 KB." });
                using var secret = new StreamReader(serviceFile.OpenReadStream(), Encoding.UTF8);
                using var config = new StreamReader(androidFile.OpenReadStream(), Encoding.UTF8);
                await service.ConfigureAsync(await secret.ReadToEndAsync(ct), await config.ReadToEndAsync(ct), ct);
                return Results.Ok(await service.StatusAsync(ct));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or CryptographicException or InvalidOperationException or HttpRequestException)
            { return Problem(ex); }
        }).DisableAntiforgery();
        phone.MapPost("/pairing", async (HttpContext context, LocalPublishingRequestGuard guard, PhoneCompanionService service, CancellationToken ct) =>
        {
            if (!guard.IsValid(context)) return Results.StatusCode(403);
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var code = await service.PairingAsync(ct);
                using var generator = new QRCodeGenerator();
                using var data = generator.CreateQrCode(code, QRCodeGenerator.ECCLevel.M);
                using var qr = new SvgQRCode(data);
                return Results.Ok(new { code, svg = qr.GetGraphic(5) });
            }
            catch (InvalidOperationException ex) { return Problem(ex); }
        });
        phone.MapPost("/sync", async (HttpContext context, LocalPublishingRequestGuard guard, PhoneCompanionService service, CancellationToken ct) =>
        {
            if (!guard.IsValid(context)) return Results.StatusCode(403);
            try { await service.SyncAsync(true, ct); return Results.Ok(await service.StatusAsync(ct)); }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException) { return Problem(ex); }
        });
        phone.MapPost("/rotate", async (HttpContext context, LocalPublishingRequestGuard guard, PhoneCompanionService service, CancellationToken ct) =>
        {
            if (!guard.IsValid(context)) return Results.StatusCode(403);
            try { await service.RotateAsync(ct); return Results.Ok(await service.StatusAsync(ct)); }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException) { return Problem(ex); }
        });
        phone.MapPost("/test", async (HttpContext context, LocalPublishingRequestGuard guard, PhoneCompanionService service, CancellationToken ct) =>
        {
            if (!guard.IsValid(context)) return Results.StatusCode(403);
            try { await service.TestPushAsync(ct); return Results.Ok(new { sent = true }); }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException) { return Problem(ex); }
        });
    }
    private static IResult Problem(Exception ex) => Results.Problem(title: "Phone app setup needs attention",
        detail: ex is InvalidOperationException ? ex.Message : "The Firebase files could not be used. Check the files and your internet connection.", statusCode: 400);
}
