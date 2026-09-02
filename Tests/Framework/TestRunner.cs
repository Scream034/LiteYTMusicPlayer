using System.Diagnostics;

namespace LMP.Tests.Framework;

/// <summary>
/// Движок запуска тестов. Выполняет тесты с таймаутами, CancellationToken,
/// и уведомляет о прогрессе через callback'и.
/// <para>
/// Все тесты запускаются в ThreadPool (не блокируют UI-поток).
/// Callback'и вызываются из фонового потока — UI-маршалинг на стороне вызывающего.
/// </para>
/// </summary>
public sealed class TestRunner
{
    private readonly IServiceProvider _services;

    /// <summary>Вызывается перед запуском каждого теста.</summary>
    public event Action<TestDescriptor>? TestStarting;

    /// <summary>Вызывается после завершения каждого теста.</summary>
    public event Action<TestDescriptor, TestResult>? TestCompleted;

    public TestRunner(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>
    /// Запускает один тест.
    /// </summary>
    /// <param name="descriptor">Дескриптор теста.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Результат выполнения.</returns>
    public async Task<TestResult> RunAsync(TestDescriptor descriptor, CancellationToken ct = default)
    {
        TestStarting?.Invoke(descriptor);

        var result = await ExecuteTestAsync(descriptor, ct);

        TestCompleted?.Invoke(descriptor, result);
        return result;
    }

    /// <summary>
    /// Запускает список тестов последовательно.
    /// Останавливается при отмене <paramref name="ct"/>, но уже запущенный тест дорабатывает.
    /// </summary>
    public async Task<IReadOnlyList<TestResult>> RunBatchAsync(
        IReadOnlyList<TestDescriptor> tests,
        CancellationToken ct = default)
    {
        var results = new List<TestResult>(tests.Count);

        foreach (var test in tests)
        {
            if (ct.IsCancellationRequested)
            {
                // Помечаем оставшиеся как Skipped
                results.Add(new TestResult
                {
                    TestId = test.Id,
                    State = TestRunState.Skipped,
                    Duration = TimeSpan.Zero,
                    ErrorMessage = "Cancelled",
                });
                continue;
            }

            var result = await RunAsync(test, ct);
            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Запускает ВСЕ обнаруженные тесты в порядке: Unit → Integration → Benchmark.
    /// </summary>
    public Task<IReadOnlyList<TestResult>> RunAllAsync(CancellationToken ct = default) =>
        RunBatchAsync(TestDiscovery.GetAllOrdered(), ct);

    // 
    // PRIVATE
    // 

    /// <summary>
    /// Выполняет тест с обёрткой: таймаут, exception handling, замер времени.
    /// Тест вызывается в ThreadPool чтобы не блокировать вызывающий поток.
    /// </summary>
    private async Task<TestResult> ExecuteTestAsync(
        TestDescriptor descriptor,
        CancellationToken externalCt)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            // Создаём linked CTS: внешний cancellation + таймаут теста
            using var timeoutCts = descriptor.TimeoutSeconds > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(descriptor.TimeoutSeconds))
                : new CancellationTokenSource();

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                externalCt, timeoutCts.Token);

            // Запускаем тест в ThreadPool
            var task = Task.Run(() => InvokeTestMethod(descriptor), linkedCts.Token);

            await task.WaitAsync(linkedCts.Token);

            sw.Stop();
            return new TestResult
            {
                TestId = descriptor.Id,
                State = TestRunState.Passed,
                Duration = sw.Elapsed,
            };
        }
        catch (OperationCanceledException) when (externalCt.IsCancellationRequested)
        {
            sw.Stop();
            return new TestResult
            {
                TestId = descriptor.Id,
                State = TestRunState.Skipped,
                Duration = sw.Elapsed,
                ErrorMessage = "Cancelled by user",
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new TestResult
            {
                TestId = descriptor.Id,
                State = TestRunState.Failed,
                Duration = sw.Elapsed,
                ErrorMessage = $"Timeout ({descriptor.TimeoutSeconds}s)",
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Разворачиваем AggregateException / TargetInvocationException
            var inner = ex is AggregateException agg ? agg.InnerException ?? ex : ex;
            if (inner is System.Reflection.TargetInvocationException tie)
                inner = tie.InnerException ?? tie;

            return new TestResult
            {
                TestId = descriptor.Id,
                State = TestRunState.Failed,
                Duration = sw.Elapsed,
                ErrorMessage = inner.Message,
            };
        }
    }

    /// <summary>
    /// Вызывает тестовый метод через рефлексию.
    /// Поддерживает сигнатуры: Task Foo() и Task Foo(IServiceProvider).
    /// </summary>
    private Task InvokeTestMethod(TestDescriptor descriptor)
    {
        object?[] args = descriptor.RequiresServices
            ? [_services]
            : [];

        var result = descriptor.Method.Invoke(null, args);

        return result is Task task
            ? task
            : Task.CompletedTask;
    }
}