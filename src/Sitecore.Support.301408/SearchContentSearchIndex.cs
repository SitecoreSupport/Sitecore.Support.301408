using Sitecore.ContentSearch;
using Sitecore.ContentSearch.Abstractions;
using Sitecore.ContentSearch.Client.Pipelines.Search;
using Sitecore.ContentSearch.Diagnostics;
using Sitecore.ContentSearch.Exceptions;
using Sitecore.ContentSearch.SearchTypes;
using Sitecore.ContentSearch.Utilities;
using Sitecore.Data.Items;
using Sitecore.Diagnostics;
using Sitecore.Pipelines.Search;
using Sitecore.Search;
using Sitecore.Shell;
using Sitecore.StringExtensions;
using System;
using System.Collections.Generic;
using System.Linq;
using Sitecore.Globalization;

namespace Sitecore.Support.ContentSearch.Client.Pipelines.Search
{
  public class SearchContentSearchIndex
  {
    private ISettings settings;

    public virtual void Process(SearchArgs args)
    {
      Assert.ArgumentNotNull(args, "args");
      if (!args.UseLegacySearchEngine)
      {
        if (!ContentSearchManager.Locator.GetInstance<IContentSearchConfigurationSettings>().ContentSearchEnabled())
        {
          args.UseLegacySearchEngine = true;
        }
        else if (!ContentSearchManager.Locator.GetInstance<ISearchIndexSwitchTracker>().IsOn)
        {
          args.IsIndexProviderOn = false;
        }
        else
        {
          Item item = args.Root ?? args.Database.GetRootItem();
          Assert.IsNotNull(item, "rootItem");
          if (!args.TextQuery.IsNullOrEmpty())
          {
            ISearchIndex index;
            try
            {
              index = ContentSearchManager.GetIndex(new SitecoreIndexableItem(item));
            }
            catch (IndexNotFoundException)
            {
              SearchLog.Log.Warn("No index found for " + item.ID);
              return;
            }
            if (!ContentSearchManager.Locator.GetInstance<ISearchIndexSwitchTracker>().IsIndexOn(index.Name))
            {
              args.IsIndexProviderOn = false;
            }
            else
            {
              if (settings == null)
              {
                settings = index.Locator.GetInstance<ISettings>();
              }
              using (IProviderSearchContext providerSearchContext = index.CreateSearchContext())
              {
                List<SitecoreUISearchResultItem> results = new List<SitecoreUISearchResultItem>();
                try
                {
                  IQueryable<SitecoreUISearchResultItem> queryable = null;
                  if (args.Type != SearchType.ContentEditor)
                  {
                    queryable = new GenericSearchIndex().Search(args, providerSearchContext);
                  }
                  if (queryable == null || Enumerable.Count(queryable) == 0)
                  {
                    if (args.ContentLanguage == null || args.ContentLanguage.Name.IsNullOrEmpty())
                    {
                      queryable = (from i in providerSearchContext.GetQueryable<SitecoreUISearchResultItem>()
                        where i.Name.StartsWith(args.TextQuery) || i.Content.Contains(args.TextQuery)
                        select i);
                    }
                    else
                    {
                      if (args.ContentLanguage.Name.StartsWith("ja"))
                      {
                        queryable = providerSearchContext.GetQueryable<SitecoreUISearchResultItem>().Where((SitecoreUISearchResultItem i) => i.Name.Equals(args.TextQuery) || (i.Content.Equals(args.TextQuery) && i.Language.Equals(args.ContentLanguage.Name)));
                      }
                      else
                      {
                        queryable = providerSearchContext.GetQueryable<SitecoreUISearchResultItem>().Where((SitecoreUISearchResultItem i) => i.Name.StartsWith(args.TextQuery) || (i.Content.Contains(args.TextQuery) && i.Language.Equals(args.ContentLanguage.Name)));
                      }
                    }
                  }
                  if (args.Root != null && args.Type != SearchType.ContentEditor)
                  {
                    queryable = from i in queryable
                                where i.Paths.Contains(args.Root.ID)
                                select i;
                  }
                  foreach (SitecoreUISearchResultItem item2 in Enumerable.TakeWhile(queryable, (SitecoreUISearchResultItem result) => results.Count < args.Limit))
                  {
                    if (!UserOptions.View.ShowHiddenItems)
                    {
                      Item sitecoreItem = GetSitecoreItem(item2);
                      if (sitecoreItem != null && IsHidden(sitecoreItem))
                      {
                        continue;
                      }
                    }
                    SitecoreUISearchResultItem sitecoreUISearchResultItem = results.FirstOrDefault((SitecoreUISearchResultItem r) => r.ItemId == item2.ItemId);
                    if (sitecoreUISearchResultItem == null)
                    {
                      results.Add(item2);
                    }
                    else if (args.ContentLanguage != null && !args.ContentLanguage.Name.IsNullOrEmpty())
                    {
                      if ((sitecoreUISearchResultItem.Language != args.ContentLanguage.Name && item2.Language == args.ContentLanguage.Name) || (sitecoreUISearchResultItem.Language == item2.Language && sitecoreUISearchResultItem.Uri.Version.Number < item2.Uri.Version.Number))
                      {
                        results.Remove(sitecoreUISearchResultItem);
                        results.Add(item2);
                      }
                    }
                    else if (args.Type != SearchType.Classic)
                    {
                      if (sitecoreUISearchResultItem.Language == item2.Language && sitecoreUISearchResultItem.Uri.Version.Number < item2.Uri.Version.Number)
                      {
                        results.Remove(sitecoreUISearchResultItem);
                        results.Add(item2);
                      }
                    }
                    else
                    {
                      results.Add(item2);
                    }
                  }
                }
                catch (Exception exception)
                {
                  Log.Error("Invalid lucene search query: " + args.TextQuery, exception, this);
                  return;
                }
                FillSearchResult(results, args);
              }
            }
          }
        }
      }
    }

    protected virtual void FillSearchResult(IList<SitecoreUISearchResultItem> searchResult, SearchArgs args)
    {
      foreach (SitecoreUISearchResultItem item in searchResult)
      {
        Item sitecoreItem = GetSitecoreItem(item);
        if (sitecoreItem != null)
        {
          string text = item.DisplayName ?? item.Name;
          if (text != null)
          {
            object obj = item.Fields.Find((KeyValuePair<string, object> pair) => pair.Key == Sitecore.Search.BuiltinFields.Icon).Value ?? sitecoreItem.Appearance.Icon ?? settings?.DefaultIcon();
            if (obj != null)
            {
              string url = string.Empty;
              if (item.Uri != null)
              {
                url = item.Uri.ToString();
              }
              args.Result.AddResult(new SearchResult(text, obj.ToString(), url));
            }
          }
        }
      }
    }

    protected virtual Item GetSitecoreItem(SitecoreUISearchResultItem searchItem)
    {
      if (searchItem == null)
      {
        return null;
      }
      try
      {
        return searchItem.GetItem();
      }
      catch (NullReferenceException)
      {
      }
      return null;
    }

    private bool IsHidden(Item item)
    {
      Assert.ArgumentNotNull(item, "item");
      if (!item.Appearance.Hidden)
      {
        if (item.Parent != null)
        {
          return IsHidden(item.Parent);
        }
        return false;
      }
      return true;
    }
  }
}