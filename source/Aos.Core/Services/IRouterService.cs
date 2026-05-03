using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public interface IRouterService
{
    RouterSelectionResult SelectModel(RouterSelectionRequest request);
}
