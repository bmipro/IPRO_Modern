-- 591: do two advisers' domain names share one certificate name in Azure? Read-only: it changes nothing.
-- No rows back is the good answer.
--
-- Until 591 every adviser's certificate was named "managed-" + the name with its dots as dashes, cut at
-- 80 characters. Two different names could share one: www.abc-def.com and www-abc.def.com, or two long
-- names that agree on their first 72 characters. Removing one of them could delete the other's
-- certificate. Each row is one such pair, or two domain rows that name the very same address (removing
-- either would have deleted the other's binding and certificate). certificate_name is the one in Azure.
-- Every name in AgentDomains counts: DomainName, RootDomain and WwwDomain, any adviser, read the way the old
-- code read it before naming a certificate (small letters, spaces and dots at either end left off).
WITH names AS (
  SELECT d.Id AS row_id, d.AgentUserId, 'DomainName' AS field, LOWER(TRIM(BOTH '.' FROM TRIM(d.DomainName))) AS host FROM AgentDomains d
  UNION ALL
  SELECT d.Id, d.AgentUserId, 'RootDomain', LOWER(TRIM(BOTH '.' FROM TRIM(d.RootDomain))) FROM AgentDomains d
  UNION ALL
  SELECT d.Id, d.AgentUserId, 'WwwDomain', LOWER(TRIM(BOTH '.' FROM TRIM(d.WwwDomain))) FROM AgentDomains d
),
hosts AS (
  SELECT row_id, AgentUserId, host,
         GROUP_CONCAT(field ORDER BY field SEPARATOR '+') AS fields,
         LEFT(CONCAT('managed-', REPLACE(host, '.', '-')), 80) AS certificate_name,
         CAST(host AS BINARY) AS host_key,
         CAST(LEFT(CONCAT('managed-', REPLACE(host, '.', '-')), 80) AS BINARY) AS certificate_key
  FROM names
  WHERE host IS NOT NULL AND host <> ''
  GROUP BY row_id, AgentUserId, host
)
SELECT a.certificate_name,
       CASE WHEN a.host_key = b.host_key THEN 'two rows name the same address'
            ELSE 'two addresses share one certificate name' END AS why,
       a.host AS host_1, a.AgentUserId AS adviser_1, u1.UserName AS username_1, a.row_id AS row_1, a.fields AS fields_1,
       b.host AS host_2, b.AgentUserId AS adviser_2, u2.UserName AS username_2, b.row_id AS row_2, b.fields AS fields_2
FROM hosts a
JOIN hosts b
  ON a.certificate_key = b.certificate_key
 AND (a.host_key < b.host_key OR (a.host_key = b.host_key AND a.row_id < b.row_id))
LEFT JOIN AgentUsers u1 ON u1.Id = a.AgentUserId
LEFT JOIN AgentUsers u2 ON u2.Id = b.AgentUserId
ORDER BY a.certificate_name, a.host_key, b.host_key, a.row_id, b.row_id;
