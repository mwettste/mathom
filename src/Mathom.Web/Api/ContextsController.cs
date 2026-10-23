using System.Security.Claims;
using Mathom.Web.Contexts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Mathom.Web.Api;

[ApiController]
[Authorize]
[Route("api/v1/contexts")]
public class ContextsController(ContextService contexts) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var list = await contexts.ListAsync(userId, ct);
        return Ok(list.Select(c => new ContextDto(c.Id, c.Name)).ToList());
    }
}
