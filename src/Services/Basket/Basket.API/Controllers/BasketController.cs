﻿using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.BuildingBlocks.EventBus.Abstractions;
using HMS.Basket.API.Model;
using HMS.Basket.API.Services;
using System;
using System.Net;
using System.Threading.Tasks;
using System.Linq;
using HMS.IntegrationEvents;
using HMS.IntegrationEvents.Events;

namespace HMS.Basket.API.Controllers
{
    [Route("api/v1/[controller]")]
    [Authorize]
    public class BasketController : Controller
    {
        private readonly IBasketRepository _repository;
        private readonly IIdentityService _identitySvc;
        private readonly IEventBus _eventBus;

        public BasketController(IBasketRepository repository,
            IIdentityService identityService,
            IEventBus eventBus)
        {
            _repository = repository;
            _identitySvc = identityService;
            _eventBus = eventBus;
        }

        // GET /id
        [HttpGet("{id}")]
        [ProducesResponseType(typeof(CustomerBasket), (int)HttpStatusCode.OK)]
        public async Task<IActionResult> Get(string id)
        {
			CustomerBasket basket = await _repository.GetBasketAsync(id);

            return Ok(basket);
        }

        // POST /value
        [HttpPost]
        [ProducesResponseType(typeof(CustomerBasket), (int)HttpStatusCode.OK)]
        public async Task<IActionResult> Post([FromBody]CustomerBasket value)
        {
			value.Items = value.Items.Where(x => x.Quantity > 0).ToList();

			value.Items = value.Items
				.GroupBy(l => l.ProductId)
				.Select(cl => {
					int sum = cl.Sum(c => c.Quantity);
					cl.First().Quantity = sum;
					return cl.First();

				}).ToList();
			CustomerBasket basket = await _repository.UpdateBasketAsync(value);

            return Ok(basket);
        }

        [Route("checkout")]
        [HttpPost]
        [ProducesResponseType((int)HttpStatusCode.Accepted)]
        [ProducesResponseType((int)HttpStatusCode.BadRequest)]
        public async Task<IActionResult> Checkout([FromBody]BasketCheckout basketCheckout, [FromHeader(Name = "x-requestid")] string requestId)
        {
			string userId = _identitySvc.GetUserIdentity();
            basketCheckout.RequestId = (Guid.TryParse(requestId, out Guid guid) && guid != Guid.Empty) ?
                guid : basketCheckout.RequestId;

			CustomerBasket basket = await _repository.GetBasketAsync(userId);

            if (basket == null)
            {
                return BadRequest();
            }

			UserCheckoutAcceptedIntegrationEvent eventMessage = new UserCheckoutAcceptedIntegrationEvent(userId, basketCheckout.City, basketCheckout.Street,
                basketCheckout.State, basketCheckout.Country, basketCheckout.ZipCode, basketCheckout.CardNumber, basketCheckout.CardHolderName,
                basketCheckout.CardExpiration, basketCheckout.CardSecurityNumber, basketCheckout.CardTypeId, basketCheckout.Buyer, basketCheckout.RequestId, basket);

            // Once basket is checkout, sends an integration event to
            // ordering.api to convert basket to order and proceeds with
            // order creation process
            _eventBus.Publish(eventMessage);            

            return Accepted();
        }

        // DELETE api/values/5
        [HttpDelete("{id}")]
        public void Delete(string id)
        {
            _repository.DeleteBasketAsync(id);
        }

    }
}
