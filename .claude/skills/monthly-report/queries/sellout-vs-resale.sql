-- Per event that went on sale in :month, the minute primary inventory first left
-- TICKETS_AVAILABLE, the resale picture at that minute, and whether primary reappeared.
-- Contract against the schema in docs/superpowers/specs/2026-09-11-nashville-concert-price-sources-research.md §4.
-- Usage: psql -v month='2026-09' -f sellout-vs-resale.sql

with primary_ticks as (
    select t.event_id, t.observed_at, t.status
    from on_sale_ticks t
    join sources s on s.id = t.source_id
    where s.kind = 'Primary'
),
onsale as (
    select e.id as event_id, e.name, v.name as venue, e.on_sale_at
    from events e
    join venues v on v.id = e.venue_id
    where to_char(e.on_sale_at, 'YYYY-MM') = :'month'
),
first_gone as (
    select p.event_id, min(p.observed_at) as gone_at
    from primary_ticks p
    join onsale o on o.event_id = p.event_id
    where p.observed_at >= o.on_sale_at
      and p.status <> 'TICKETS_AVAILABLE'
    group by p.event_id
),
reappeared as (
    select p.event_id, min(p.observed_at) as back_at
    from primary_ticks p
    join first_gone g on g.event_id = p.event_id
    where p.observed_at > g.gone_at and p.status = 'TICKETS_AVAILABLE'
    group by p.event_id
),
resale_at_gone as (
    select distinct on (t.event_id) t.event_id, t.listing_count, t.lowest
    from on_sale_ticks t
    join sources s on s.id = t.source_id and s.kind = 'Resale'
    join first_gone g on g.event_id = t.event_id
    where t.observed_at <= g.gone_at
    order by t.event_id, t.observed_at desc
)
select o.venue,
       o.name,
       o.on_sale_at,
       round(extract(epoch from (g.gone_at - o.on_sale_at)) / 60) as minutes_to_primary_gone,
       r.listing_count                                            as resale_listings_at_that_minute,
       r.lowest                                                   as resale_lowest_at_that_minute,
       b.back_at is not null                                      as primary_reappeared,
       round(extract(epoch from (b.back_at - g.gone_at)) / 3600, 1) as hours_until_reappeared
from onsale o
left join first_gone g on g.event_id = o.event_id
left join reappeared b on b.event_id = o.event_id
left join resale_at_gone r on r.event_id = o.event_id
order by o.on_sale_at;
