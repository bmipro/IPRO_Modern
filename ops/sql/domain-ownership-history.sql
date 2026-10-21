-- 592: which custom domains changed adviser, or stopped being an adviser's? Read-only: it changes nothing.
--
-- iPro keeps no history of domain rows, so this reads what the database remembers instead: the address every
-- page view and every lead arrived on (WebsitePageViews.SourceDomain, WebsiteLeads.SourceDomain), and whose
-- site that was. One row per finding:
--   "served for one adviser, now another adviser's row"  -- visitors reached adviser A's site on this name, and
--                                                           today a domain row of adviser B names it
--   "served for an adviser, no row names it now"         -- visitors reached an adviser's site on this name, and
--                                                           no domain row, website or setup address names it now
--                                                           (removed; if its DNS still points at iPro, anyone could
--                                                           add it before 592)
--   "row's website belongs to another adviser"           -- a domain row of one adviser pointing at another
--                                                           adviser's website (moved between accounts)
--   "removed, Azure not yet confirmed"                   -- since 592: removed, its Azure binding still to go
--   "removal refused: not a plain host name"             -- since 592: a removed row whose stored name iPro will not
--                                                           send to Azure (a dot or space at either end, an accent);
--                                                           look in Azure by hand for the binding it had
-- The page views and leads of a deleted adviser are deleted with them, so a domain that left with a deleted
-- account shows only through the Azure check (ops/sql/orphan-bindings.sql). iPro's own names and the free sites
-- (*.247advisers.com) are left out. No rows back is the good answer; otherwise send the rows.
WITH seen AS (
  SELECT LOWER(TRIM(BOTH '.' FROM TRIM(v.SourceDomain))) AS host, w.AgentUserId AS adviser,
         MIN(v.CreatedAt) AS first_seen, MAX(v.CreatedAt) AS last_seen, COUNT(*) AS visits
  FROM WebsitePageViews v
  JOIN AgentWebsites w ON w.Id = v.AgentWebsiteId
  WHERE v.SourceDomain <> ''
  GROUP BY LOWER(TRIM(BOTH '.' FROM TRIM(v.SourceDomain))), w.AgentUserId
  UNION ALL
  SELECT LOWER(TRIM(BOTH '.' FROM TRIM(l.SourceDomain))), l.AgentUserId, MIN(l.CreatedAt), MAX(l.CreatedAt), COUNT(*)
  FROM WebsiteLeads l
  WHERE l.SourceDomain <> ''
  GROUP BY LOWER(TRIM(BOTH '.' FROM TRIM(l.SourceDomain))), l.AgentUserId
),
custom AS (
  SELECT s.host, s.adviser, MIN(s.first_seen) AS first_seen, MAX(s.last_seen) AS last_seen, SUM(s.visits) AS visits
  FROM seen s
  WHERE s.host LIKE '%.%'
    AND NOT s.host REGEXP '(^|[.])(iproadvisers|iproaccountants|ipromortgages|247advisers)[.]com$|(^|[.])crm[.]to$|(^|[.])azurewebsites[.]net$|(^|[.])localhost$'
  GROUP BY s.host, s.adviser
),
named AS (
  SELECT d.Id AS row_id, d.AgentUserId, LOWER(TRIM(BOTH '.' FROM TRIM(d.DomainName))) AS host FROM AgentDomains d
  UNION ALL
  SELECT d.Id, d.AgentUserId, LOWER(TRIM(BOTH '.' FROM TRIM(d.WwwDomain))) FROM AgentDomains d WHERE d.WwwDomain <> ''
  UNION ALL
  SELECT d.Id, d.AgentUserId, LOWER(TRIM(BOTH '.' FROM TRIM(d.RootDomain))) FROM AgentDomains d WHERE d.RootDomain <> ''
),
findings AS (
  SELECT DISTINCT c.host, 'served for one adviser, now another adviser''s row' AS finding,
         c.adviser AS earlier_adviser, n.AgentUserId AS adviser_now, n.row_id, c.first_seen, c.last_seen, c.visits
  FROM custom c
  JOIN named n ON n.host = c.host AND n.AgentUserId <> c.adviser
  UNION ALL
  SELECT c.host, 'served for an adviser, no row names it now', c.adviser, NULL, NULL, c.first_seen, c.last_seen, c.visits
  FROM custom c
  WHERE NOT EXISTS (SELECT 1 FROM named n WHERE n.host = c.host)
    AND NOT EXISTS (SELECT 1 FROM AgentWebsites w WHERE LOWER(TRIM(BOTH '.' FROM TRIM(w.CustomDomain))) = c.host)
    AND NOT EXISTS (SELECT 1 FROM AgentUsers u WHERE LOWER(TRIM(BOTH '.' FROM TRIM(u.DomainName))) = c.host)
  UNION ALL
  SELECT LOWER(d.DomainName), 'row''s website belongs to another adviser', w.AgentUserId, d.AgentUserId, d.Id, d.CreatedAt, d.UpdatedAt, NULL
  FROM AgentDomains d
  JOIN AgentWebsites w ON w.Id = d.AgentWebsiteId AND w.AgentUserId <> d.AgentUserId
  UNION ALL
  SELECT LOWER(c.HostName), 'removed, Azure not yet confirmed', c.FormerAgentUserId, NULL, NULL, c.CreatedAt, c.LastAttemptAt, c.Attempts
  FROM DomainCleanups c
  WHERE c.DoneAt IS NULL
  UNION ALL
  SELECT LOWER(c.HostName), 'removal refused: not a plain host name', c.FormerAgentUserId, NULL, NULL, c.CreatedAt, c.LastAttemptAt, c.Attempts
  FROM DomainCleanups c
  WHERE c.Outcome = 'refused: not an adviser''s name'
    AND NOT LOWER(c.HostName) REGEXP '(^|[.])(iproadvisers|iproaccountants|ipromortgages|247advisers)[.]com$|(^|[.])crm[.]to$|(^|[.])azurewebsites[.]net$'
)
SELECT f.host, f.finding,
       f.earlier_adviser, u1.UserName AS earlier_username,
       f.adviser_now, u2.UserName AS username_now,
       f.row_id, f.first_seen, f.last_seen, f.visits
FROM findings f
LEFT JOIN AgentUsers u1 ON u1.Id = f.earlier_adviser
LEFT JOIN AgentUsers u2 ON u2.Id = f.adviser_now
ORDER BY f.host, f.finding, f.earlier_adviser;
