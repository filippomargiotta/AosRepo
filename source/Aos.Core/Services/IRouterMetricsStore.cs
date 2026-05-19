using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public interface IRouterMetricsStore
{
    bool TryGetMetric(
        string taskClass,
        RouterModelCandidate candidate,
        out RouterModelPerformanceMetric? metric);

    IReadOnlyList<RouterModelPerformanceMetric> ListMetrics(string taskClass);
}
