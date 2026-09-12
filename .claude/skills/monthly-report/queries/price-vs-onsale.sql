-- Per event with a final price in :month, each source's final lowest price as a percentage
-- of the primary on-sale price (the first primary tick with a price at or after on-sale).
-- Usage: psql -v month='2026-09' -f price-vs-onsale.sql

with onsale_price as (
    select distinct on (t.event_id) t.event_id, t.lowest as onsale_lowest, t.all_in as onsale_all_in
    from on_sale_ticks t
    join sources s on s.id = t.source_id and s.kind = 'Primary'
    join events e on e.id = t.event_id
    where t.lowest is not null and t.observed_at >= e.on_sale_at
    order by t.event_id, t.observed_at
)
select v.name as venue,
       e.name,
       e.starts_at,
       s.name as source,
       s.kind,
       f.lowest as final_lowest,
       f.all_in as final_all_in,
       o.onsale_lowest,
       o.onsale_all_in,
       case when f.all_in = o.onsale_all_in
            then round(100 * f.lowest / nullif(o.onsale_lowest, 0), 0)
            else null end as pct_of_onsale  -- null when the two numbers are not the same kind
from final_prices f
join sources s on s.id = f.source_id
join events e on e.id = f.event_id
join venues v on v.id = e.venue_id
left join onsale_price o on o.event_id = f.event_id
where to_char(e.starts_at, 'YYYY-MM') = :'month'
order by e.starts_at, s.kind, s.name;
