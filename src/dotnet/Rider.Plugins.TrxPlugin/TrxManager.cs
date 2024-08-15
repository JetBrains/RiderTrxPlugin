﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Linq;
using System.Threading;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.Rd.Tasks;
using JetBrains.ReSharper.Feature.Services.Protocol;
using Rider.Plugins.TrxPlugin.Model;
using JetBrains.ReSharper.UnitTestFramework.Execution;
using JetBrains.ReSharper.UnitTestFramework.Session;
using JetBrains.ReSharper.UnitTestFramework.UI.Session;
using System.Xml.Serialization;
using JetBrains.Annotations;
using JetBrains.ReSharper.UnitTestFramework;
using JetBrains.ReSharper.UnitTestFramework.Caching;
using JetBrains.ReSharper.UnitTestFramework.Criteria;
using JetBrains.ReSharper.UnitTestFramework.Elements;
using JetBrains.ReSharper.UnitTestFramework.Persistence;
using JetBrains.ReSharper.UnitTestFramework.Transient;
using JetBrains.ReSharper.UnitTestFramework.UI.ViewModels;
using JetBrains.Util;
using JetBrains.Util.Dotnet.TargetFrameworkIds;
using Rider.Plugins.TrxPlugin.TrxNodes;
using UnitTestResult = Rider.Plugins.TrxPlugin.TrxNodes.UnitTestResult;

namespace Rider.Plugins.TrxPlugin;

[SolutionComponent]
public class TrxManager
{
    [NotNull] private readonly Lifetime myLifetime;
    [NotNull] private readonly TransientTestProvider myTransientTestProvider;
    [NotNull] private readonly IUnitTestElementRepository myElementRepository;
    [NotNull] private readonly IUnitTestSessionRepository mySessionRepository;
    [NotNull] private readonly IUnitTestSessionConductor mySessionConductor;
    [NotNull] private readonly IUnitTestingProjectCache myProjectCache;
    [NotNull] private readonly ILogger myLogger;
    [NotNull] private readonly IUnitTestResultManager myResultManager;
    [NotNull] private readonly RdTrxPluginModel myModel;
    [NotNull] private readonly ISolution mySolution;

    public TrxManager(
        Lifetime lifetime,
        TransientTestProvider transientTestProvider,
        IUnitTestElementRepository elementRepository,
        IUnitTestSessionRepository repository,
        IUnitTestSessionConductor sessionConductor,
        IUnitTestResultManager resultManager,
        IUnitTestingProjectCache projectCache,
        ILogger logger,
        ISolution solution)
    {
        myLifetime = lifetime;
        myTransientTestProvider = transientTestProvider;
        myElementRepository = elementRepository;
        mySessionRepository = repository;
        mySessionConductor = sessionConductor;
        myResultManager = resultManager;
        myProjectCache = projectCache;
        myLogger = logger;
        myModel = solution.GetProtocolSolution().GetRdTrxPluginModel();
        mySolution = solution;
        myModel.ImportTrxCall.SetAsync(HandleCall);
    }

    public List<UnitTestResult> ParseResults(XElement node)
    {
        var results = new List<UnitTestResult>();
        foreach (var result in node.Elements())
        {
            if (result.Name.LocalName == "UnitTestResult")
            {
                var serializer = new XmlSerializer(typeof(UnitTestResult),
                    "http://microsoft.com/schemas/VisualStudio/TeamTest/2010");
                var startNode = new XElement(result);
                try
                {
                    using (var reader = startNode.CreateReader())
                    {
                        var unitTestResult = (UnitTestResult)serializer.Deserialize(reader);
                        if (unitTestResult == null)
                        {
                            continue;
                        }

                        results.Add(unitTestResult);
                    }
                }
                catch (Exception ex)
                {
                    myLogger.Error(ex);
                }
            }
            else
            {
                results.AddRange(ParseResults(result));
            }
        }

        return results;
    }

    private void AddDefinitions(XElement node, List<UnitTestResult> results)
    {

        foreach (var element in node.Elements())
        {
            if (element.Name.LocalName == "UnitTest")
            {
                var serializer = new XmlSerializer(typeof(UnitTest), "http://microsoft.com/schemas/VisualStudio/TeamTest/2010");
                var startNode = new XElement(element);
                try
                {
                    using (var reader = startNode.CreateReader())
                    {
                        var unitTest = (UnitTest)serializer.Deserialize(reader);
                        foreach (var result in results)
                        {
                            if (result.Id == unitTest?.Execution.Id)
                            {
                                result.Definition = unitTest;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    myLogger.Error(ex);
                }
            }
            else
            {
                AddDefinitions(element, results);
            }
        }
    }

    private void AddInnerResults(UnitTestResult result, List<UnitTestResult> results)
    {
        if (result.InnerResults == null)
        {
            return;
        }

        foreach (var innerResult in result.InnerResults.UnitTestResults)
        {
            results.Add(innerResult);
            AddInnerResults(innerResult, results);
        }
    }

    private IUnitTestElement TestElementCreator(UnitTestResult current, IUnitTestTransaction tx, HashSet<IUnitTestElement> elements)
    {
        UnitTestElementNamespace ns =
            UnitTestElementNamespace.Create(current.Definition.TestMethod.ClassName);
        TransientTestElement element = new TransientTestElement(current.TestName, ns)
        {
            NaturalId = UT.CreateId(myProjectCache.GetProject(mySolution.SolutionDirectory.ToString()),
                TargetFrameworkId.Default,
                (IUnitTestProvider)this.myTransientTestProvider,
                current.Definition.TestMethod.ClassName + current.TestName)
        };

        if (elements.Contains(element))
        {
            return null;
        }

        if (current.InnerResults != null)
        {
            foreach (var child in current.InnerResults.UnitTestResults)
            {
                var childElement = TestElementCreator(child, tx, elements);
                if (childElement != null)
                {
                    childElement.Parent = (IUnitTestElement)element;
                    tx.Create((IUnitTestElement)childElement);
                    elements.Add((IUnitTestElement)childElement);
                }
            }
        }

        return element;
    }


    private async Task<bool> HandleTrx(string trxFilePath)
    {
        XDocument document;
        try
        {
            await using (var stream = File.OpenRead(trxFilePath))
            {
                document = await XDocument.LoadAsync(stream, LoadOptions.None, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            myLogger.Error(ex);
            return false;
        }

        var root = document.Root;
        if (root == null)
        {
            return false;
        }

        var results = ParseResults(root);
        var countOuterResults = results.Count;
        for (int i = 0; i < countOuterResults; i++)
        {
            AddInnerResults(results[i], results);
        }

        AddDefinitions(root, results);
        await DisplayResults((CancellationToken)myLifetime, results);
        return true;
    }

    private async Task DisplayResults(CancellationToken ct, List<UnitTestResult> results)
    {
        try
        {
            myElementRepository.Clear();
            IUnitTestSession
                session = this.mySessionRepository.CreateSession(NothingCriterion.Instance, "Imported");
            HashSet<IUnitTestElement> elements = new HashSet<IUnitTestElement>();
            IUnitTestTransactionCommitResult transactionCommitResult = await this.myElementRepository.BeginTransaction(
                (Action<IUnitTestTransaction>)(tx =>
                {
                    foreach (var result in results)
                    {
                        var outerElement = TestElementCreator(result, tx, elements);
                        if (outerElement != null)
                        {
                            tx.Create(outerElement);
                            elements.Add(outerElement);
                        }
                    }
                }), ct);
            UT.Facade.Append(
                    (IUnitTestElementCriterion)new TestElementCriterion((IEnumerable<IUnitTestElement>)elements)).To
                .Session(session);
            foreach (var element in elements)
            {
                var result = results.FirstOrDefault(r =>
                    r.Definition.TestMethod.ClassName == element.GetNamespace().ToString() &&
                    r.TestName == element.ShortName);

                if (result == null)
                {
                    continue;
                }

                switch (result.Outcome?.ToLower())
                {
                    case null:
                        break;
                    case "passed":
                        myResultManager.TestFinishing(element, session, UnitTestStatus.Success, null,
                            TimeSpan.Parse(result.Duration ?? "0", CultureInfo.InvariantCulture));
                        break;
                    case "failed":
                        myResultManager.TestFinishing(element, session, UnitTestStatus.Failed,
                            result.Output?.ErrorInfo?.Message,
                            TimeSpan.Parse(result.Duration ?? "0", CultureInfo.InvariantCulture));
                        var exceptions = new List<TestException>
                        {
                            new TestException(null, result.Output?.ErrorInfo?.Message,
                                result.Output?.ErrorInfo?.StackTrace)
                        };
                        myResultManager.TestException(element, session, exceptions);
                        break;
                    case "aborted":
                        myResultManager.TestFinishing(element, session, UnitTestStatus.Aborted,
                            null, TimeSpan.Parse(result.Duration ?? "0", CultureInfo.InvariantCulture));
                        break;
                    case "running":
                        myResultManager.TestFinishing(element, session, UnitTestStatus.Running,
                            null, TimeSpan.Parse(result.Duration ?? "0", CultureInfo.InvariantCulture));
                        break;
                    case "inconclusive":
                        myResultManager.TestFinishing(element, session, UnitTestStatus.Inconclusive, null,
                            TimeSpan.Parse(result.Duration ?? "0", CultureInfo.InvariantCulture));
                        break;
                    case "pending":
                        myResultManager.TestFinishing(element, session, UnitTestStatus.Pending,
                            null, TimeSpan.Parse(result.Duration ?? "0", CultureInfo.InvariantCulture));
                        break;
                    case "notexecuted":
                        myResultManager.TestFinishing(element, session, UnitTestStatus.Ignored,
                            result.Output?.ErrorInfo?.Message);
                        break;
                    default:
                        myResultManager.TestFinishing(element, session, UnitTestStatus.Unknown,
                            null, TimeSpan.Parse(result.Duration ?? "0", CultureInfo.InvariantCulture));
                        break;
                }

                myResultManager.TestOutput(element, session, result.Output?.StdOut, TestOutputType.STDOUT);
            }

            IUnitTestSessionTreeViewModel sessionTreeViewModel = await this.mySessionConductor.OpenSession(session);
            sessionTreeViewModel.Grouping.Value = new UnitTestingGroupingSelection(UnitTestSessionTreeGroupings
                .GetSessionProviders(mySolution, session)
                .Where(p => p.Key == "Namespace").ToArray());
            session = (IUnitTestSession)null;
        }
        catch (Exception ex)
        {
            this.myLogger.Error(ex);
        }
    }

    private async Task<RdCallResponse> HandleCall(Lifetime lt, RdCallRequest request)
    {
        string path = request.TrxPath;
        if (await HandleTrx(path))
        {
            return lt.Execute(() => new RdCallResponse("Success"));
        }

        return lt.Execute(() => new RdCallResponse("Failed"));
    }
}
