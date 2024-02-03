﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using CoffeeLands.Data;
using CoffeeLands.Models;
using Newtonsoft.Json;
using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;
using PayPal.Api;
using Microsoft.Extensions.Configuration;
using CoffeeLands.Services;



namespace CoffeeLands.Controllers
{
    public class OrdersController : Controller
    {
        private readonly CoffeeLandsContext _context;
        private IHttpContextAccessor _contextAccessor;
        IConfiguration _configuration;
        private IVNPayService _vnPayService;
        private readonly IMailService mailService;
        public OrdersController(CoffeeLandsContext context, IHttpContextAccessor contextAccessor, IConfiguration iconfiguration, IVNPayService vnPayService, IMailService mailService)
        {
            _context = context;
            _contextAccessor = contextAccessor;
            _configuration = iconfiguration;
            _vnPayService = vnPayService;
            this.mailService = mailService;
        }

        // Index Orders
        public async Task<IActionResult> Index(string sortOrder, string currentFilter, string searchString, int? pageNumber)
        {
            ViewData["CurrentSort"] = sortOrder;
            ViewData["IDSortParm"] = String.IsNullOrEmpty(sortOrder) ? "id_desc" : "";
            ViewData["NameSortParm"] = sortOrder == "name_asc" ? "name_asc" : "name_desc";
            
            if (searchString != null)
            {
                pageNumber = 1;
            }
            else
            {
                searchString = currentFilter;
            }

            ViewData["CurrentFilter"] = searchString;

            var orders = from o in _context.OrderProduct
                           .Include(u => u.User)
                         select o;
            if (!String.IsNullOrEmpty(searchString))
            {
                orders = orders.Where(s => s.Name.Contains(searchString));
            }
            switch (sortOrder)
            {
                case "id_desc":
                    orders = orders.OrderByDescending(s => s.Id);
                    break;
                case "name_asc":
                    orders = orders.OrderBy(s => s.Name);
                    break;
                case "name_desc":
                    orders = orders.OrderByDescending(s => s.Name);
                    break;
                default:
                    orders = orders.OrderBy(s => s.Id);
                    break;
            }

            int pageSize = 10;
            return View(await PaginatedList<OrderProduct>.CreateAsync(orders.AsNoTracking(), pageNumber ?? 1, pageSize));
        }


        #region Checkout
        public async Task<IActionResult> Checkout()
        {
            var checkUser = HttpContext.Session.GetString("UserSession");
            if (string.IsNullOrEmpty(checkUser))
            {
                return RedirectToAction("Login", "Users");
            }
            else
            {
                ViewBag.MySession = checkUser.ToString();
            }

            ViewBag.CartNumber = HttpContext.Session.GetString("CartNumber");
            var productListJson = HttpContext.Session.GetString("Cart");
            if (!string.IsNullOrEmpty(productListJson))
            {
                decimal totalProduct = 0;
                decimal subtotal = 0;
                var productList = JsonConvert.DeserializeObject<List<ProductCart>>(productListJson);
                ViewBag.Cart = productList;
                foreach (ProductCart productCart in productList)
                {
                    totalProduct = productCart.Qty * productCart.CartProduct.Price;
                    subtotal += totalProduct;
                }
                ViewBag.TotalProduct = totalProduct;
                ViewBag.Subtotal = subtotal;
                ViewBag.GrandTotal = Math.Round(subtotal * 1.10m, 2);
            }
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PlaceOrder([Bind("Name,Email,Tel,Address,Status,Grand_total,Shipping_method,Payment_method,UserID")] OrderProduct order, decimal grandTotal, string shipping_method, string payment_method)
        {
            var checkUser = HttpContext.Session.GetString("UserSession");
            ViewBag.MySession = checkUser.ToString();
            var user = await _context.User
                .Include(pc => pc.ProductCarts)
                .FirstOrDefaultAsync(m => m.Name == checkUser);

            if (true)
            {
                if (user != null)
                {
                    try
                    {
                        order.Grand_total = grandTotal;

                        if (shipping_method == "express")
                        {
                            order.Shipping_method = "Express";
                        }
                        else if (shipping_method == "free_shipping")
                        {
                            order.Shipping_method = "Free Ship";
                        }

                        if (payment_method == "vnpay")
                        {
                            order.Payment_method = "VnPay";
                        }
                        else if (payment_method == "paypal")
                        {
                            order.Payment_method = "Paypal";
                        }
                        else if (payment_method == "momo")
                        {
                            order.Payment_method = "Momo";
                        }
                        else if (payment_method == "COD")
                        {
                            order.Payment_method = "COD";
                        }

                        order.UserID = user.Id;
                        _context.Add(order);
                        await _context.SaveChangesAsync();

                        var productListJson = HttpContext.Session.GetString("Cart");

                        if (!string.IsNullOrEmpty(productListJson))
                        {
                            var productList = JsonConvert.DeserializeObject<List<ProductCart>>(productListJson);
                            var orderDetails = new List<OrderDetail>();

                            foreach (ProductCart cart in productList)
                            {
                                OrderDetail orderDetail = new OrderDetail
                                {
                                    OrderProductID = order.Id,
                                    ProductID = cart.CartProduct.Id,
                                    Qty = cart.Qty,
                                    Price = cart.CartProduct.Price * cart.Qty
                                };
                                
                                orderDetails.Add(orderDetail);
                            }
                            _context.AddRange(orderDetails);
                            await _context.SaveChangesAsync();

                            var orderID = order.Id;
                            HttpContext.Session.SetInt32("OrderID", orderID);
                            ViewBag.OrderProductList = orderDetails;
                            var data = new ThankYouRequest
                            {
                                ToEmail = order.Email,
                                UserName = order.Name
                            };

                            if (order.Payment_method == "Paypal")
                            {                              
                                var grandTotalll = order.Grand_total.ToString();
                                HttpContext.Session.SetString("GrandTotalOrder", grandTotalll);
                                ViewBag.GranTotall = grandTotalll;
                                return RedirectToAction("PaymentWithPaypal");
                            }

                            if (order.Payment_method == "VnPay")
                            {
                                var vnPayModel = new VnPaymentRequestModel
                                {
                                    Amount = (double)order.Grand_total,
                                    CreatedDate = DateTime.Now,
                                    Description = $"{order.Name} {order.Tel}",
                                    FullName = order.Name,
                                    OrderId = new Random().Next(1000, 10000)
                                };

                                return Redirect(_vnPayService.CreatePaymentUrl(HttpContext, vnPayModel));
                            }

                            productList.Clear();
                            string updatedProductListJson = JsonConvert.SerializeObject(productList);
                            HttpContext.Session.SetString("Cart", updatedProductListJson);
                            HttpContext.Session.SetString("CartNumber", productList.Count.ToString());

                            if (order.Payment_method == "COD")
                            {
                                
                                ViewBag.CartNumber = HttpContext.Session.GetString("CartNumber");
                                var orderSuccess = await _context.OrderProduct
                                    .Include(o => o.User)
                                    .Include(od => od.OrderDetails)
                                    .ThenInclude(p => p.Product)
                                    .FirstOrDefaultAsync(m => m.Id == order.Id);

                                await mailService.SendThankYouEmailAsync(data);

                                return View("~/Views/Orders/Thankyou.cshtml", orderSuccess);

                            }
                            await mailService.SendThankYouEmailAsync(data);
                            return View("~/Views/Orders/Payment.cshtml", order);
                        }
                    }
                    catch (DbUpdateException)
                    {
                        ModelState.AddModelError("", "Unable to save changes. Try again, and if the problem persists see your system administrator.");
                    }
                }
            }
            return View("~/Views/Orders/Checkout.cshtml");
        }

        public async Task<IActionResult> Pay(int? id)
        {
            var checkUser = HttpContext.Session.GetString("UserSession");
            if (string.IsNullOrEmpty(checkUser))
            {
                return RedirectToAction("Login", "Users");
            }
            else
            {
                ViewBag.MySession = checkUser.ToString();
            }
            ViewBag.CartNumber = HttpContext.Session.GetString("CartNumber");

            var orderUpdate = await _context.OrderProduct
                .Include(o => o.User)
                .Include(od => od.OrderDetails)
                .ThenInclude(p => p.Product)
                .FirstOrDefaultAsync(m => m.Id == id);

            if (orderUpdate == null)
            {
                return NotFound();
            }
            if (true)
            {
                try
                {
                    orderUpdate.Is_paid = true;
                    orderUpdate.Status = OrderStatus.CONFIRMED;
                    _context.Update(orderUpdate);
                    await _context.SaveChangesAsync();
                    return View("~/Views/Orders/Thankyou.cshtml", orderUpdate);
                }
                catch (DbUpdateException /* ex */)
                {
                    ModelState.AddModelError("", "Unable to save changes. " +
                        "Try again, and if the problem persists, " +
                        "see your system administrator.");
                }
            }
            return View("~/Views/Orders/Payment.cshtml");
        }

        public async Task<IActionResult> Repayment(int? id)
        {
            var orderRepayment = await _context.OrderProduct
               .Include(o => o.User)
               .Include(od => od.OrderDetails)
               .ThenInclude(p => p.Product)
               .FirstOrDefaultAsync(m => m.Id == id);
            return View("~/Views/Orders/Repayment.cshtml", orderRepayment);
        }

        [HttpPost]
        public async Task<IActionResult> RepaymentPost(int? id, string payment_method)
        {
            var orderUpdate = await _context.OrderProduct
               .Include(o => o.User)
               .Include(od => od.OrderDetails)
               .ThenInclude(p => p.Product)
               .FirstOrDefaultAsync(m => m.Id == id);
            if(orderUpdate != null)
            {
            if (true)
            {
                try
                {
                        if (payment_method == "vnpay")
                        {
                            orderUpdate.Payment_method = "VnPay";
                        }
                        else if (payment_method == "paypal")
                        {
                            orderUpdate.Payment_method = "Paypal";
                        }
                        else if (payment_method == "momo")
                        {
                            orderUpdate.Payment_method = "Momo";
                        }
                        else if (payment_method == "COD")
                        {
                            orderUpdate.Payment_method = "COD";
                        }

                        _context.Update(orderUpdate);
                        await _context.SaveChangesAsync();

                        var orderID = orderUpdate.Id;
                            HttpContext.Session.SetInt32("OrderID", orderID);
                            
                            var data = new ThankYouRequest
                            {
                                ToEmail = orderUpdate.Email,
                                UserName = orderUpdate.Name
                            };

                            if (orderUpdate.Payment_method == "Paypal")
                            {
                                var grandTotalll = orderUpdate.Grand_total.ToString();
                                HttpContext.Session.SetString("GrandTotalOrder", grandTotalll);
                                ViewBag.GranTotall = grandTotalll;
                                return RedirectToAction("PaymentWithPaypal");
                            }

                            if (orderUpdate.Payment_method == "VnPay")
                            {
                                var vnPayModel = new VnPaymentRequestModel
                                {
                                    Amount = (double)orderUpdate.Grand_total,
                                    CreatedDate = DateTime.Now,
                                    Description = $"{orderUpdate.Name} {orderUpdate.Tel}",
                                    FullName = orderUpdate.Name,
                                    OrderId = new Random().Next(1000, 10000)
                                };

                                return Redirect(_vnPayService.CreatePaymentUrl(HttpContext, vnPayModel));
                            }

                            if (orderUpdate.Payment_method == "COD")
                            {
                                ViewBag.CartNumber = HttpContext.Session.GetString("CartNumber");
                                var orderSuccess = await _context.OrderProduct
                                    .Include(o => o.User)
                                    .Include(od => od.OrderDetails)
                                .ThenInclude(p => p.Product)
                                    .FirstOrDefaultAsync(m => m.Id == orderUpdate.Id);

                                await mailService.SendThankYouEmailAsync(data);

                                return View("~/Views/Orders/Thankyou.cshtml", orderSuccess);

                            }


                        return View("~/Views/Orders/Payment.cshtml", orderUpdate);
                    }
                catch (DbUpdateException /* ex */)
                {
                    //Log the error (uncomment ex variable name and write a log.)
                    ModelState.AddModelError("", "Unable to save changes. " +
                        "Try again, and if the problem persists, " +
                        "see your system administrator.");
                }
            }
            }
            return View("~/Views/Orders/Repayment.cshtml");
        }
        #endregion

        #region paypal
        public async Task<IActionResult> PaymentWithPaypal(string Cancel = null, string blogId = "", string PayerID = "", string guid = "")
        {
            ViewBag.MySession = HttpContext.Session.GetString("UserSession");
            var orderID = HttpContext.Session.GetInt32("OrderID");
            //var checkUser = HttpContext.Session.GetString("UserSession");
            //if (string.IsNullOrEmpty(checkUser))
            //{
            //    return RedirectToAction("Login", "Users");
            //}
            //else
            //{
            //    ViewBag.MySession = checkUser.ToString();
            //}
            var productListJson = HttpContext.Session.GetString("Cart");

            var orderUpdate = await _context.OrderProduct
                .Include(o => o.User)
                .Include(od => od.OrderDetails)
                .ThenInclude(p => p.Product)
                 .FirstOrDefaultAsync(m => m.Id == orderID);

            if (orderUpdate != null)
            {
                var ClientID = _configuration.GetValue<string>("PayPal:Key");
                var ClientSecret = _configuration.GetValue<string>("PayPal:Secret");
                var mode = _configuration.GetValue<string>("PayPal:mode");
                APIContext apiContext = PaypalConfiguration.GetAPIContext(ClientID, ClientSecret, mode);
                try
                {
                    string payerId = PayerID;
                    if (string.IsNullOrEmpty(payerId))
                    {
                        string baseUrl = this.Request.Scheme + "://" + this.Request.Host + "/Orders/PaymentWithPayPal?";
                        var guidd = Convert.ToString((new Random()).Next(100000));
                        guid = guidd;
                        var createdPayment = this.CreatePayment(apiContext, baseUrl + "guid=" + guid, blogId);
                        var links = createdPayment.links.GetEnumerator();
                        string paypalRedirectUrl = null;
                        while (links.MoveNext())
                        {
                            Links lnk = links.Current;
                            if (lnk.rel.ToLower().Trim().Equals("approval_url"))
                            {
                                paypalRedirectUrl = lnk.href;
                            }
                        }
                        HttpContext.Session.SetString("payment", createdPayment.id);
                        return Redirect(paypalRedirectUrl);
                    }
                    else
                    {
                        var paymentId = HttpContext.Session.GetString("payment");
                        var executePayment = ExecutePayment(apiContext, payerId, paymentId as string);
                        if (executePayment.state.ToLower() != "approved")
                        {
                            if (productListJson != null)
                            {
                                var productList = JsonConvert.DeserializeObject<List<ProductCart>>(productListJson);
                                productList.Clear();
                                string updatedProductListJson = JsonConvert.SerializeObject(productList);
                                HttpContext.Session.SetString("Cart", updatedProductListJson);
                                HttpContext.Session.SetString("CartNumber", productList.Count.ToString());
                            }
                            HttpContext.Session.Remove("OrderID");
                            ViewBag.CartNumber = HttpContext.Session.GetString("CartNumber");
                            ViewBag.Result = "false";
                            return View("~/Views/Orders/Thankyou.cshtml", orderUpdate);
                        }
                        var blogIds = executePayment.transactions[0].item_list.items[0].sku;
                        orderUpdate.Is_paid = true;
                        orderUpdate.Status = OrderStatus.CONFIRMED;
                        _context.Update(orderUpdate);
                        await _context.SaveChangesAsync();

                        if (productListJson != null)
                        {
                            var productList = JsonConvert.DeserializeObject<List<ProductCart>>(productListJson);
                            productList.Clear();
                            string updatedProductListJson = JsonConvert.SerializeObject(productList);
                            HttpContext.Session.SetString("Cart", updatedProductListJson);
                            HttpContext.Session.SetString("CartNumber", productList.Count.ToString());
                        }
                        HttpContext.Session.Remove("OrderID");
                        ViewBag.CartNumber = HttpContext.Session.GetString("CartNumber");
                        var data = new ThankYouRequest
                        {
                            ToEmail = orderUpdate.Email,
                            UserName = orderUpdate.Name
                        };
                        await mailService.SendThankYouEmailAsync(data);
                        return View("~/Views/Orders/Thankyou.cshtml", orderUpdate);
                    }
                }
                catch (Exception ex)
                {
                    return View("~/Views/Orders/Fail.cshtml");
                }
            }

            return View("~/Views/Orders/Fail.cshtml");
        }

        private PayPal.Api.Payment payment;
        private IHttpContextAccessor? contextAccessor;

        private Payment ExecutePayment(APIContext apiContext, string payerId, string paymentId)
        {
            var paymentExecution = new PaymentExecution()
            {
                payer_id = payerId
            };
            this.payment = new Payment()
            {
                id = paymentId
            };
            return this.payment.Execute(apiContext, paymentExecution);
        }

        private Payment CreatePayment(APIContext apiContext, string redirectUrl, string blogId)
        {
            var cart = HttpContext.Session.GetString("Cart");
            var grandTotal = HttpContext.Session.GetString("GrandTotalOrder");
            
            var orderPaypal = JsonConvert.DeserializeObject<List<ProductCart>>(cart);
            decimal subTotal = 0;
            //decimal grandTotal = 0;
            var itemList = new ItemList()
            {
                items = new List<Item>()
            };
            foreach (var item in orderPaypal)
            {
                var price = item.CartProduct.Price * item.Qty;
                subTotal += price;
                itemList.items.Add(new Item()
                {
                    name = item.CartProduct.Name,
                    currency = "USD",
                    price = price.ToString(),
                    quantity = item.Qty.ToString(),
                    sku = "asd"
                });
            }
            
            var payer = new PayPal.Api.Payer()
            {
                payment_method = "paypal"
            };
            var redirUrls = new RedirectUrls()
            {
                cancel_url = redirectUrl + "&Cancel=true",
                return_url = redirectUrl
            };

            //var details = new Details()
            //{
            //    tax = "1",
            //    shipping = "1",
            //    subtotal = "1"
            //};

            var amount = new PayPal.Api.Amount()
            {
                currency = "USD",
                total = subTotal.ToString()
            };
            var transactionList = new List<Transaction>();
            transactionList.Add(new Transaction()
            {
                description = "Transaction description",
                invoice_number = Guid.NewGuid().ToString(),
                amount = amount,
                item_list = itemList
            });
            this.payment = new Payment()
            {
                intent = "sale",
                payer = payer,
                transactions = transactionList,
                redirect_urls = redirUrls
            };
            return this.payment.Create(apiContext);
        }

        public IActionResult Success()
        {
            return View();
        }

        public IActionResult Fail()
        {
            return View();
        }
        #endregion

        #region Payment VNPay
        public async Task<IActionResult> PaymentCallBack()
        {
            var response = _vnPayService.PaymentExcute(Request.Query);
            ViewBag.MySession = HttpContext.Session.GetString("UserSession");
            //ViewBag.MySession = checkUser.ToString();      

            var orderID = HttpContext.Session.GetInt32("OrderID");
            var productListJson = HttpContext.Session.GetString("Cart");
            var orderUpdate = await _context.OrderProduct
                .Include(o => o.User)
                .Include(od => od.OrderDetails)
                .ThenInclude(p => p.Product)
                 .FirstOrDefaultAsync(m => m.Id == orderID);

            if (orderUpdate != null)
            {
                if (response == null || response.VnPayResponseCode != "00")
                {
                    TempData["Message"] = $"Lỗi thanh toán vnpay: {response.VnPayResponseCode}";
                    if (productListJson != null)
                    {
                        var productList = JsonConvert.DeserializeObject<List<ProductCart>>(productListJson);
                        productList.Clear();
                        string updatedProductListJson = JsonConvert.SerializeObject(productList);
                        HttpContext.Session.SetString("Cart", updatedProductListJson);
                        HttpContext.Session.SetString("CartNumber", productList.Count.ToString());
                    }
                    HttpContext.Session.Remove("OrderID");
                    ViewBag.CartNumber = HttpContext.Session.GetString("CartNumber");
                    ViewBag.Result = "false";
                    return View("~/Views/Orders/Thankyou.cshtml", orderUpdate);
                    //return RedirectToAction("Fail");
                }

                TempData["Message"] = $"Thanh toán VNPay thành công";
                orderUpdate.Is_paid = true;
                orderUpdate.Status = OrderStatus.CONFIRMED;
                _context.Update(orderUpdate);
                await _context.SaveChangesAsync();
                if (productListJson != null)
                {
                    var productList = JsonConvert.DeserializeObject<List<ProductCart>>(productListJson);
                    productList.Clear();
                    string updatedProductListJson = JsonConvert.SerializeObject(productList);
                    HttpContext.Session.SetString("Cart", updatedProductListJson);
                    HttpContext.Session.SetString("CartNumber", productList.Count.ToString());
                }
                HttpContext.Session.Remove("OrderID");
                ViewBag.CartNumber = HttpContext.Session.GetString("CartNumber");
                var data = new ThankYouRequest
                {
                    ToEmail = orderUpdate.Email,
                    UserName = orderUpdate.Name
                };
                await mailService.SendThankYouEmailAsync(data);
                return View("~/Views/Orders/Thankyou.cshtml", orderUpdate);
            }
            return View("~/Views/Orders/Checkout.cshtml");
        }
        #endregion

        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var order = await _context.OrderProduct
                .Include(o => o.User)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (order == null)
            {
                return NotFound();
            }

            return View(order);
        }

        private bool OrderExists(int id)
        {
            return _context.OrderProduct.Any(e => e.Id == id);
        }
    }
}
