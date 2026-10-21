-- 592: which addresses are bound on the web app in Azure while nothing in iPro names them? Read-only: it changes
-- nothing.
--
-- A binding left behind keeps the address pointed at iPro; before 592 the next adviser to add it got it, with
-- its visitors (www.411trades.com). The list between the two marker lines is the web app's bindings, as
-- `az webapp config hostname list -g ipro-production --webapp-name ipro-prod-web` gave them on the date below;
-- ops/domain-ownership/orphan-bindings.sh prints this file again with a fresh list. One row per address:
--   "bound in Azure, no domain row or website names it"  -- an orphan: nobody's site, still bound
--   "bound in Azure, named only by a website's own field" -- a website's CustomDomain holds it with no domain row:
--                                                           since 592 that site is no longer shown on it
--   "bound in Azure, named only by a setup address"      -- an adviser's setup address (SuperAdmin -> Edit) names
--                                                           it, with no domain row: attached without any proof
-- iPro's own names are left out. Removing a binding is the owner's decision.
WITH bound (host) AS (
  -- bound names: begin (2026-10-19)
  SELECT '*.247advisers.com'
  UNION ALL SELECT 'app.iproadvisers.com'
  UNION ALL SELECT 'crm.to'
  UNION ALL SELECT 'iproaccountants.com'
  UNION ALL SELECT 'iproadvisers.com'
  UNION ALL SELECT 'ipromortgages.com'
  UNION ALL SELECT 'ipro-prod-web.azurewebsites.net'
  UNION ALL SELECT 'lavenuebakery.com'
  UNION ALL SELECT 'www.411trades.com'
  UNION ALL SELECT 'www.4ipro.com'
  UNION ALL SELECT 'www.crm.to'
  UNION ALL SELECT 'www.drhug.ca'
  UNION ALL SELECT 'www.fortressconsys.com'
  UNION ALL SELECT 'www.iproaccountants.com'
  UNION ALL SELECT 'www.iproadvisers.com'
  UNION ALL SELECT 'www.ipromortgages.com'
  UNION ALL SELECT 'www.lavenuebakery.ca'
  UNION ALL SELECT 'www.lavenuebakery.com'
  UNION ALL SELECT 'www.ouritems.ca'
  -- bound names: end
),
named AS (
  SELECT LOWER(TRIM(BOTH '.' FROM TRIM(d.DomainName))) AS host FROM AgentDomains d
  UNION ALL SELECT LOWER(TRIM(BOTH '.' FROM TRIM(d.WwwDomain))) FROM AgentDomains d
  UNION ALL SELECT LOWER(TRIM(BOTH '.' FROM TRIM(d.RootDomain))) FROM AgentDomains d
)
SELECT b.host,
       CASE WHEN EXISTS (SELECT 1 FROM AgentWebsites w WHERE LOWER(TRIM(BOTH '.' FROM TRIM(w.CustomDomain))) = LOWER(b.host))
            THEN 'bound in Azure, named only by a website''s own field'
            WHEN EXISTS (SELECT 1 FROM AgentUsers u WHERE LOWER(TRIM(BOTH '.' FROM TRIM(u.DomainName))) = LOWER(b.host))
            THEN 'bound in Azure, named only by a setup address'
            ELSE 'bound in Azure, no domain row or website names it' END AS finding,
       (SELECT GROUP_CONCAT(u.UserName ORDER BY u.Id SEPARATOR ', ') FROM AgentUsers u
         WHERE LOWER(TRIM(BOTH '.' FROM TRIM(u.DomainName))) = LOWER(b.host)) AS setup_address_of
FROM bound b
WHERE NOT LOWER(b.host) REGEXP '(^|[.])(iproadvisers|iproaccountants|ipromortgages|247advisers)[.]com$|(^|[.])crm[.]to$|(^|[.])azurewebsites[.]net$'
  AND NOT EXISTS (SELECT 1 FROM named n WHERE n.host = LOWER(b.host))
ORDER BY b.host;
